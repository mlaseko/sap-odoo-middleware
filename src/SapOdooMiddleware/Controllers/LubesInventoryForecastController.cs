using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Services.Reports;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Lubes inventory coverage/forecast report. Read-only; runs the coverage SQL against the Molas
/// <b>Lubes</b> SAP B1 database (NOT Autohub) and returns one row per stock item with on-hand, open-PO,
/// rolling 3/6-month outflow, average monthly out, months of cover, and a coverage status bucket.
/// </summary>
[ApiController]
[Route("api/lubes")]
public sealed class LubesInventoryForecastController : ControllerBase
{
    private readonly ILubesInventoryForecastService _service;
    private readonly ILogger<LubesInventoryForecastController> _logger;

    public LubesInventoryForecastController(
        ILubesInventoryForecastService service, ILogger<LubesInventoryForecastController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/lubes/inventory-forecast
    /// Returns the inventory coverage/forecast for every Lubes stock item (inventory items that are
    /// valid and not frozen). Computed server-side over OITM/OITW/OPOR/POR1/OINM.
    /// </summary>
    [HttpGet("inventory-forecast")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<LubesInventoryForecastRow>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInventoryForecast(CancellationToken ct)
    {
        try
        {
            var rows = await _service.GetForecastAsync(ct);
            var meta = new Dictionary<string, object> { ["count"] = rows.Count };
            return Ok(ApiResponse<IReadOnlyList<LubesInventoryForecastRow>>.Ok(rows, meta));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lubes inventory forecast failed.");
            return StatusCode(500, ApiResponse<IReadOnlyList<LubesInventoryForecastRow>>.Fail(ex.Message));
        }
    }
}
