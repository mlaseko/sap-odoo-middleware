using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Autohub (MOLAS_Live_2021) Item Master API: item-group dropdown data, item
/// creation with fixed defaults (Inventory/Sales/Purchase = Y, VAT TZ/TZS, no
/// Manufacturer) + PL01-PL05 prices, and a narrow field update.
/// Requires the <c>X-Api-Key</c> header.
/// </summary>
[ApiController]
[Route("api/sap")]
public class SapItemsController : ControllerBase
{
    private readonly IAutohubSapB1Service _sap;
    private readonly IAutohubInventorySqlService _sql;
    private readonly IOitmRefreshQueueRepository _refreshQueue;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SapItemsController> _logger;

    public SapItemsController(
        IAutohubSapB1Service sap, IAutohubInventorySqlService sql,
        IOitmRefreshQueueRepository refreshQueue, IMemoryCache cache,
        ILogger<SapItemsController> logger)
    {
        _sap = sap;
        _sql = sql;
        _refreshQueue = refreshQueue;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/sap/items?q={text}&amp;limit={n}
    /// Live SAP item search for warehouse staff holding an OEM number, article
    /// number, part of the name, or part of the code. Case-insensitive and
    /// space/dash-insensitive across ItemCode, ItemName, U_Article_No and
    /// U_OE_Numbers ("/"-joined). Ranked: ItemCode prefix → exact article/OE
    /// match → contains. Same element shape as GET /items/{itemCode}, except
    /// prices are not populated (speed). No match → 200 with an empty array.
    /// </summary>
    [HttpGet("items")]
    [ProducesResponseType(typeof(ApiResponse<List<SapItemMasterDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SearchItems(
        [FromQuery(Name = "q")] string? q,
        [FromQuery(Name = "limit")] int limit = 20,
        CancellationToken ct = default)
    {
        var raw = q?.Trim() ?? "";
        if (raw.Length is < 2 or > 50)
            return BadRequest(ApiResponse<object>.Fail("q is required (2 to 50 characters)."));

        // Match ignoring spaces and dashes, per the agreed contract.
        var normalized = raw.Replace(" ", "").Replace("-", "");
        if (normalized.Length < 2)
            return BadRequest(ApiResponse<object>.Fail("q must contain at least 2 searchable characters."));

        limit = Math.Clamp(limit, 1, 50);

        try
        {
            var items = await _sql.SearchItemMasterAsync(normalized, limit, ct);
            return Ok(ApiResponse<List<SapItemMasterDto>>.Ok(items));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Item Master search failed for q={Query}", raw);
            return StatusCode(500, ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// GET /api/sap/items/{itemCode}
    /// The live item master row: item name, group, the four editable UDFs plus
    /// U_OE_Numbers, active/frozen flags, total on-hand and PL01-PL05 prices.
    /// Direct SQL read (no DI API seat), so the frontend can call it on every
    /// item view and does not depend on the Neon products mirror.
    /// </summary>
    [HttpGet("items/{itemCode}")]
    [ProducesResponseType(typeof(ApiResponse<SapItemMasterDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetItem(string itemCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
            return BadRequest(ApiResponse<object>.Fail("itemCode is required in the URL."));
        try
        {
            var item = await _sql.GetItemMasterAsync(itemCode.Trim(), ct);
            if (item is null)
                return NotFound(ApiResponse<object>.Fail($"Item '{itemCode.Trim()}' does not exist in SAP."));
            return Ok(ApiResponse<SapItemMasterDto>.Ok(item));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Item Master read failed for {ItemCode}", itemCode);
            return StatusCode(500, ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// GET /api/sap/item-groups
    /// SAP Item Groups (OITB) for the frontend dropdown — the user sees the name,
    /// the frontend submits the code. Cached 10 minutes. Returns a bare array:
    /// <c>[{"code":105,"name":"VAG"}]</c>.
    /// </summary>
    [HttpGet("item-groups")]
    [ProducesResponseType(typeof(List<SapItemGroupDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetItemGroups(CancellationToken ct)
    {
        var groups = await GetCachedGroupsAsync(ct);
        return Ok(groups.Select(g => new SapItemGroupDto(g.Code, g.Name)).ToList());
    }

    /// <summary>
    /// POST /api/sap/items
    /// Creates an Autohub item. Fixed by the backend: PurchaseItem/SalesItem/
    /// InventoryItem = Yes, VatGroupSales = TZ, VatGroupPurchases = TZS, no standard
    /// Manufacturer/FirmCode (U_ItemManufacturer mirrors U_MdlTEST, the brand truth
    /// field). Optional prices for PL01-PL05 (TZS). After the SAP commit the item is
    /// published to the Neon <c>oitm_refresh_queue</c> (all four tracked fields) so the
    /// DGX worker creates + enriches it without waiting for nightly reconciliation.
    /// </summary>
    [HttpPost("items")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateItem(
        [FromBody] SapItemCreateApiRequest request, CancellationToken ct)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.ItemCode))
            errors.Add("itemCode is required.");
        else if (request.ItemCode.Trim().Length > 50)
            errors.Add("itemCode must be at most 50 characters (SAP limit).");
        if (string.IsNullOrWhiteSpace(request.ItemName))
            errors.Add("itemName is required.");
        if (request.ItemGroupCode <= 0)
            errors.Add("itemGroupCode is required (pick it from GET /api/sap/item-groups).");
        if (request.Prices is not null)
        {
            foreach (var (listNum, price) in request.Prices.ToListNumMap())
                if (price < 0m)
                    errors.Add($"prices: PL{listNum:00} cannot be negative.");
        }
        if (errors.Count > 0)
            return BadRequest(ApiResponse<object>.Fail(errors));

        request.ItemCode = request.ItemCode.Trim();
        request.ItemName = request.ItemName.Trim();

        try
        {
            // Group must be a real OITB group so a stale frontend can't mis-file items.
            var groups = await GetCachedGroupsAsync(ct);
            if (!groups.Any(g => g.Code == request.ItemGroupCode))
                return BadRequest(ApiResponse<object>.Fail(
                    $"itemGroupCode {request.ItemGroupCode} does not exist in SAP — refresh the dropdown."));

            await _sap.CreateItemMasterAsync(request, ct);

            // SAP has committed — publish the new item to the Neon refresh queue so the
            // DGX worker creates + enriches it without waiting for nightly reconciliation.
            var queued = await TryEnqueueNeonRefreshForCreateAsync(request);

            return Ok(ApiResponse<object>.Ok(new
            {
                item_code = request.ItemCode,
                item_group_code = request.ItemGroupCode,
                prices_set = request.Prices?.ToListNumMap().Count ?? 0,
                neon_refresh_queued = queued,
            }));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
        {
            return Conflict(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Item Master create failed for {ItemCode}", request.ItemCode);
            return StatusCode(500, ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// PATCH /api/sap/items/{itemCode}
    /// Updates ONLY ItemName, U_MdlTEST, U_Item_Name, U_Article_No. Omitted (null)
    /// fields are left untouched; no other Item Master fields are editable here.
    /// After the SAP write commits, an identity change (name / article number / brand /
    /// OEM numbers) is published to the Neon <c>oitm_refresh_queue</c> for the DGX
    /// worker; a Neon failure never fails this call (nightly reconciliation catches it).
    /// </summary>
    [HttpPatch("items/{itemCode}")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateItem(
        string itemCode, [FromBody] SapItemUpdateApiRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
            return BadRequest(ApiResponse<object>.Fail("itemCode is required in the URL."));
        if (request.ItemName is null && request.UMdlTest is null
            && request.UItemName is null && request.UArticleNo is null)
            return BadRequest(ApiResponse<object>.Fail(
                "Provide at least one of: itemName, U_MdlTEST, U_Item_Name, U_Article_No."));

        var code = itemCode.Trim();

        // Pre-edit snapshot for the queue row's before_value / change detection. Best
        // effort: a failed read must not block the user's SAP update.
        OitmIdentitySnapshot? before = null;
        try
        {
            before = await _sql.GetItemIdentitySnapshotAsync(code, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Pre-update OITM snapshot failed for {ItemCode}; queue row will carry no before_value.", code);
        }

        try
        {
            await _sap.UpdateItemMasterFieldsAsync(code, request, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Item Master update failed for {ItemCode}", itemCode);
            return StatusCode(500, ApiResponse<object>.Fail(ex.Message));
        }

        // SAP has committed — only now may the Neon refresh-queue row exist. Runs on
        // CancellationToken.None so a client disconnect can't drop the queue write.
        var (queued, changedFields) = await TryEnqueueNeonRefreshAsync(code, request, before);

        return Ok(ApiResponse<object>.Ok(new
        {
            item_code = code,
            neon_refresh_queued = queued,
            changed_fields = changedFields,
        }));
    }

    /// <summary>
    /// POST /api/sap/items/align-manufacturer?dry_run=true
    /// One-time repair (decision 26 Sep 2026): U_MdlTEST is the brand truth field, so
    /// set U_ItemManufacturer = U_MdlTEST wherever the mirror is empty or differs
    /// (case/whitespace-insensitive). dry_run=true (the default) only lists the rows;
    /// dry_run=false applies each fix through the DI API (GetByKey → set UDF → Update)
    /// so SAP's own validation and logging apply. Writes NO refresh-queue rows: the
    /// brand the catalogue carries (U_MdlTEST) is unchanged by this repair.
    /// </summary>
    [HttpPost("items/align-manufacturer")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> AlignManufacturer(
        [FromQuery(Name = "dry_run")] bool dryRun = true, CancellationToken ct = default)
    {
        try
        {
            var mismatches = await _sql.GetManufacturerMismatchesAsync(ct);
            var rows = new List<object>();
            int aligned = 0, failed = 0, skipped = 0;

            foreach (var row in mismatches)
            {
                var brand = row.UMdlTest?.Trim();
                if (string.IsNullOrEmpty(brand))
                {
                    skipped++;
                    rows.Add(new
                    {
                        item_code = row.ItemCode,
                        manufacturer_before = row.Manufacturer,
                        status = "skipped: U_MdlTEST is empty — nothing to copy",
                    });
                    continue;
                }
                if (dryRun)
                {
                    rows.Add(new
                    {
                        item_code = row.ItemCode,
                        brand,
                        manufacturer_before = row.Manufacturer,
                        status = "would set U_ItemManufacturer = U_MdlTEST",
                    });
                    continue;
                }
                try
                {
                    await _sap.SetItemManufacturerAsync(row.ItemCode, brand, ct);
                    aligned++;
                    rows.Add(new
                    {
                        item_code = row.ItemCode,
                        brand,
                        manufacturer_before = row.Manufacturer,
                        status = "aligned",
                    });
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Manufacturer alignment failed for {ItemCode}", row.ItemCode);
                    rows.Add(new
                    {
                        item_code = row.ItemCode,
                        brand,
                        manufacturer_before = row.Manufacturer,
                        status = $"failed: {ex.Message}",
                    });
                }
            }

            return Ok(ApiResponse<object>.Ok(new
            {
                dry_run = dryRun,
                total = mismatches.Count,
                aligned,
                failed,
                skipped,
                rows,
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manufacturer alignment run failed.");
            return StatusCode(500, ApiResponse<object>.Fail(ex.Message));
        }
    }

    // ── Neon refresh queue (oitm_refresh_queue) ──────────────────────────

    /// <summary>
    /// Publishes the just-committed SAP identity change to the Neon refresh queue.
    /// Never throws: SAP is the system of record, so a Neon failure is logged and the
    /// call still succeeds (the DGX nightly reconciliation catches the missed item).
    /// Only identity fields queue a row — a U_MdlTEST/brand, U_Item_Name/name,
    /// U_Article_No/article or ItemName/OEM-chain change; a no-op edit queues nothing.
    /// </summary>
    private async Task<(bool Queued, List<string> ChangedFields)> TryEnqueueNeonRefreshAsync(
        string itemCode, SapItemUpdateApiRequest request, OitmIdentitySnapshot? before)
    {
        var changed = new List<string>();
        try
        {
            // Post-commit snapshot: the actual stored values (incl. SAP-side truncation)
            // and the row's UpdateDate/UpdateTS.
            OitmIdentitySnapshot? after = null;
            try
            {
                after = await _sql.GetItemIdentitySnapshotAsync(itemCode, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Post-update OITM snapshot failed for {ItemCode}; building after_value from the request.", itemCode);
            }
            after ??= new OitmIdentitySnapshot
            {
                ItemCode = itemCode,
                OemChain = !string.IsNullOrWhiteSpace(request.ItemName) ? request.ItemName : before?.OemChain,
                UItemName = request.UItemName ?? before?.UItemName,
                UArticleNo = request.UArticleNo ?? before?.UArticleNo,
                UMdlTest = request.UMdlTest ?? before?.UMdlTest,
                Manufacturer = before?.Manufacturer,
            };

            // Which of the worker's four tracked fields actually changed. Without a
            // before snapshot, every field the request supplied counts as changed.
            if (request.UItemName is not null
                && (before is null || Differs(before.UItemName, after.UItemName)))
                changed.Add("name");
            if (request.UArticleNo is not null
                && (before is null || Differs(before.UArticleNo, after.UArticleNo)))
                changed.Add("article_number");
            if (request.UMdlTest is not null
                && (before is null || Differs(before.UMdlTest, after.UMdlTest)))
                changed.Add("brand");
            if (!string.IsNullOrWhiteSpace(request.ItemName)
                && (before is null || Differs(before.OemChain, after.OemChain)))
                changed.Add("oem_numbers");

            if (changed.Count == 0) return (false, changed);

            await _refreshQueue.EnqueueAsync(
                itemCode, changed,
                before is null ? null : ToRefreshValues(before),
                ToRefreshValues(after),
                after.SapUpdateTs, request.RequestedBy, CancellationToken.None);

            _logger.LogInformation(
                "Neon refresh queued for {ItemCode} (fields: {Fields}).", itemCode, string.Join(",", changed));
            return (true, changed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Neon oitm_refresh_queue insert failed for {ItemCode} — SAP is already updated; " +
                "the nightly DGX reconciliation will pick the item up.", itemCode);
            return (false, changed);
        }
    }

    /// <summary>
    /// Queue payload for a freshly created item: no before_value, all four tracked
    /// fields marked changed, so the DGX worker creates the Neon row from after_value
    /// and fully enriches it instead of waiting for the nightly reconciliation.
    /// Never throws — SAP is the system of record.
    /// </summary>
    private async Task<bool> TryEnqueueNeonRefreshForCreateAsync(SapItemCreateApiRequest request)
    {
        try
        {
            OitmIdentitySnapshot? after = null;
            try
            {
                after = await _sql.GetItemIdentitySnapshotAsync(request.ItemCode, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Post-create OITM snapshot failed for {ItemCode}; building after_value from the request.",
                    request.ItemCode);
            }
            after ??= new OitmIdentitySnapshot
            {
                ItemCode = request.ItemCode,
                OemChain = request.ItemName,
                UItemName = request.UItemName,
                UArticleNo = request.UArticleNo,
                UMdlTest = request.UMdlTest,
            };

            var changed = new List<string> { "name", "article_number", "brand", "oem_numbers" };
            await _refreshQueue.EnqueueAsync(
                request.ItemCode, changed, before: null, ToRefreshValues(after),
                after.SapUpdateTs, request.RequestedBy, CancellationToken.None);

            _logger.LogInformation("Neon refresh queued for created item {ItemCode}.", request.ItemCode);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Neon oitm_refresh_queue insert failed for created item {ItemCode} — the SAP item exists; " +
                "the nightly DGX reconciliation will pick it up.", request.ItemCode);
            return false;
        }
    }

    /// <summary>Maps the OITM snapshot onto the worker's JSON contract. Brand comes from
    /// U_MdlTEST — the brand truth field (decision 26 Sep 2026); U_ItemManufacturer is
    /// only a mirror of it and on ~62 legacy rows holds a stale value, so preferring it
    /// would queue the OLD brand after a brand edit.</summary>
    private static OitmRefreshValues ToRefreshValues(OitmIdentitySnapshot s) => new()
    {
        ItemName = s.UItemName,
        ArticleNumber = s.UArticleNo,
        Brand = s.UMdlTest,
        OemChain = s.OemChain,
    };

    private static bool Differs(string? a, string? b) =>
        !string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.Ordinal);

    private async Task<List<(int Code, string Name)>> GetCachedGroupsAsync(CancellationToken ct)
        => (await _cache.GetOrCreateAsync("autohub-sap-item-groups", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await _sap.GetItemGroupsAsync(ct);
        }))!;
}
