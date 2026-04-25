using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Endpoints for SAP B1 inventory data.
/// </summary>
[ApiController]
[Route("api/inventory")]
public class InventoryController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly ILogger<InventoryController> _logger;

    public InventoryController(ISapB1Service sapService, ILogger<InventoryController> logger)
    {
        _sapService = sapService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/inventory/valuation/total
    /// Returns the total on-hand inventory value in TZS as computed by SAP B1.
    /// Requires the <c>X-Api-Key</c> header.
    /// </summary>
    /// <param name="asOfDate">
    /// Optional date (YYYY-MM-DD) to evaluate inventory as-of.
    /// Defaults to today's server date when omitted.
    /// </param>
    [HttpGet("valuation/total")]
    [ProducesResponseType(typeof(ApiResponse<InventoryValuationTotalResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<InventoryValuationTotalResponse>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetValuationTotal([FromQuery(Name = "as_of_date")] DateOnly? asOfDate = null)
    {
        var effectiveDate = asOfDate ?? DateOnly.FromDateTime(DateTime.Now);

        _logger.LogInformation(
            "Inventory valuation total requested for as_of_date={AsOfDate}",
            effectiveDate.ToString("yyyy-MM-dd"));

        try
        {
            decimal total = await _sapService.GetInventoryValuationTotalAsync(asOfDate);

            var response = new InventoryValuationTotalResponse
            {
                Currency = "TZS",
                AsOfDate = effectiveDate,
                TotalInventoryValueTzs = total
            };

            return Ok(ApiResponse<InventoryValuationTotalResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inventory valuation total failed for as_of_date={AsOfDate}.", effectiveDate.ToString("yyyy-MM-dd"));
            return StatusCode(500, ApiResponse<InventoryValuationTotalResponse>.Fail(ex.Message));
        }
    }
}
