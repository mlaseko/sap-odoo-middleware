using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
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
    private readonly IMemoryCache _cache;
    private readonly ILogger<SapItemsController> _logger;

    public SapItemsController(
        IAutohubSapB1Service sap, IAutohubInventorySqlService sql, IMemoryCache cache,
        ILogger<SapItemsController> logger)
    {
        _sap = sap;
        _sql = sql;
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
    /// InventoryItem = Yes, VatGroupSales = TZ, VatGroupPurchases = TZS, no
    /// Manufacturer. Optional prices for PL01-PL05 (TZS).
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

            return Ok(ApiResponse<object>.Ok(new
            {
                item_code = request.ItemCode,
                item_group_code = request.ItemGroupCode,
                prices_set = request.Prices?.ToListNumMap().Count ?? 0,
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

        try
        {
            await _sap.UpdateItemMasterFieldsAsync(itemCode.Trim(), request, ct);
            return Ok(ApiResponse<object>.Ok(new { item_code = itemCode.Trim() }));
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
    }

    private async Task<List<(int Code, string Name)>> GetCachedGroupsAsync(CancellationToken ct)
        => (await _cache.GetOrCreateAsync("autohub-sap-item-groups", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await _sap.GetItemGroupsAsync(ct);
        }))!;
}
