using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Inventory;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Read endpoints for the Molas Autohub inventory app (MOLAS_Live_2021).
/// Reads go through direct SQL (<see cref="IAutohubInventorySqlService"/>);
/// document writes (Phase 2+) go through the DI API posting path.
/// Requires the <c>X-Api-Key</c> header.
/// </summary>
[ApiController]
[Route("api/autohub/inv")]
public class AutohubInventoryController : ControllerBase
{
    private readonly IAutohubInventorySqlService _sql;
    private readonly IBinResolver _binResolver;
    private readonly ILogger<AutohubInventoryController> _logger;

    public AutohubInventoryController(
        IAutohubInventorySqlService sql,
        IBinResolver binResolver,
        ILogger<AutohubInventoryController> logger)
    {
        _sql = sql;
        _binResolver = binResolver;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/autohub/inv/warehouses
    /// All warehouses with their bin-activation flag (cached ~5 min).
    /// </summary>
    [HttpGet("warehouses")]
    [ProducesResponseType(typeof(ApiResponse<List<WarehouseInfo>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWarehouses(CancellationToken ct)
    {
        var warehouses = await _sql.GetWarehousesAsync(ct);
        return Ok(ApiResponse<List<WarehouseInfo>>.Ok(warehouses));
    }

    /// <summary>
    /// GET /api/autohub/inv/stock?item_code=BM10001&amp;whs_code=001
    /// Normalized stock for one item: per-bin detail (OIBQ) for bin warehouses,
    /// warehouse on-hand (OITW) for non-bin warehouse 01. One row per warehouse,
    /// including item name, article number, and manufacturer.
    /// </summary>
    /// <param name="itemCode">SAP ItemCode (required).</param>
    /// <param name="whsCode">Optional warehouse filter; all stocked warehouses when omitted.</param>
    [HttpGet("stock")]
    [ProducesResponseType(typeof(ApiResponse<List<StockResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<List<StockResponse>>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetStock(
        [FromQuery(Name = "item_code")] string? itemCode,
        [FromQuery(Name = "whs_code")] string? whsCode = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
            return BadRequest(ApiResponse<List<StockResponse>>.Fail(
                "item_code query parameter is required."));

        var stock = await _sql.GetStockAsync(itemCode.Trim(), NormalizeWhs(whsCode), ct);
        return Ok(ApiResponse<List<StockResponse>>.Ok(
            stock,
            new Dictionary<string, object>
            {
                ["item_code"] = itemCode.Trim(),
                ["total_on_hand"] = stock.Sum(s => s.TotalOnHand),
            }));
    }

    /// <summary>
    /// GET /api/autohub/inv/bins?item_code=BM10001&amp;whs_code=001&amp;direction=source
    /// Bin resolver output for one item + warehouse (spec §7):
    /// <c>auto</c> (bin pre-selected), <c>options</c> (user confirms, ≤5),
    /// <c>required</c> (user must scan), or <c>not_bin_managed</c> (warehouse 01).
    /// </summary>
    /// <param name="itemCode">SAP ItemCode (required).</param>
    /// <param name="whsCode">Warehouse code (required).</param>
    /// <param name="direction"><c>source</c> (issue/transfer out) or <c>destination</c> (receipt/transfer in). Default <c>source</c>.</param>
    [HttpGet("bins")]
    [ProducesResponseType(typeof(ApiResponse<BinResolution>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<BinResolution>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResolveBins(
        [FromQuery(Name = "item_code")] string? itemCode,
        [FromQuery(Name = "whs_code")] string? whsCode,
        [FromQuery(Name = "direction")] string direction = "source",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
            return BadRequest(ApiResponse<BinResolution>.Fail("item_code query parameter is required."));
        if (string.IsNullOrWhiteSpace(whsCode))
            return BadRequest(ApiResponse<BinResolution>.Fail("whs_code query parameter is required."));

        BinDirection dir;
        switch (direction.Trim().ToLowerInvariant())
        {
            case "source": dir = BinDirection.Source; break;
            case "destination": dir = BinDirection.Destination; break;
            default:
                return BadRequest(ApiResponse<BinResolution>.Fail(
                    "direction must be 'source' or 'destination'."));
        }

        try
        {
            var resolution = await _binResolver.ResolveAsync(itemCode.Trim(), whsCode.Trim(), dir, ct);
            return Ok(ApiResponse<BinResolution>.Ok(
                resolution,
                new Dictionary<string, object>
                {
                    ["item_code"] = itemCode.Trim(),
                    ["whs_code"] = whsCode.Trim(),
                    ["direction"] = dir.ToString().ToLowerInvariant(),
                }));
        }
        catch (InvalidOperationException ex)
        {
            // Unknown warehouse etc. — caller error, not a server fault.
            return BadRequest(ApiResponse<BinResolution>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// GET /api/autohub/inv/transfer-requests?from_whs=003&amp;to_whs=001
    /// Open transfer request lines (OWTQ/WTQ1, spec §8.1) for the fulfillment screen,
    /// oldest first, with item name, article number, and manufacturer per line.
    /// </summary>
    /// <param name="fromWhs">Optional from-warehouse filter.</param>
    /// <param name="toWhs">Optional to-warehouse filter.</param>
    [HttpGet("transfer-requests")]
    [ProducesResponseType(typeof(ApiResponse<List<OpenTransferRequestLine>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOpenTransferRequests(
        [FromQuery(Name = "from_whs")] string? fromWhs = null,
        [FromQuery(Name = "to_whs")] string? toWhs = null,
        CancellationToken ct = default)
    {
        var lines = await _sql.GetOpenTransferRequestsAsync(
            NormalizeWhs(fromWhs), NormalizeWhs(toWhs), ct);

        return Ok(ApiResponse<List<OpenTransferRequestLine>>.Ok(
            lines,
            new Dictionary<string, object>
            {
                ["total_lines"] = lines.Count,
                ["total_documents"] = lines.Select(l => l.DocEntry).Distinct().Count(),
            }));
    }

    private static string? NormalizeWhs(string? whs) =>
        string.IsNullOrWhiteSpace(whs) ? null : whs.Trim();
}
