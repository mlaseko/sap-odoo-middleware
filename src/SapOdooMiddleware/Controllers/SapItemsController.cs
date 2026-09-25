using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

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
    private static readonly HashSet<string> ValidPriceLists =
        new(StringComparer.OrdinalIgnoreCase) { "PL01", "PL02", "PL03", "PL04", "PL05" };

    private readonly IAutohubSapB1Service _sap;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SapItemsController> _logger;

    public SapItemsController(
        IAutohubSapB1Service sap, IMemoryCache cache, ILogger<SapItemsController> logger)
    {
        _sap = sap;
        _cache = cache;
        _logger = logger;
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
            foreach (var (list, price) in request.Prices)
            {
                if (!ValidPriceLists.Contains(list.Trim()))
                    errors.Add($"prices: unknown price list '{list}' — valid keys are PL01..PL05.");
                if (price < 0m)
                    errors.Add($"prices: {list} cannot be negative.");
            }
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
                prices_set = request.Prices?.Count ?? 0,
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
