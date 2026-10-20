using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.MonthEnd;
using SapOdooMiddleware.Services.MonthEnd;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Month-end figures from SAP (spec v4 §7, Phase 5), read by the control centre's month-end record: supplier
/// balances per CardCode, the GRNI value, the perpetual inventory value and the landed cost credits per code.
/// Read-only; dates are <c>YYYY-MM-DD</c>; <c>as_of</c> defaults to today.
/// </summary>
[ApiController]
[Route("api/month-end")]
public class MonthEndController : ControllerBase
{
    private readonly IMonthEndReader _reader;
    private readonly ILogger<MonthEndController> _logger;

    public MonthEndController(IMonthEndReader reader, ILogger<MonthEndController> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>GET /api/month-end/supplier-balances?as_of= — every supplier's balance at the date (credit-positive).</summary>
    [HttpGet("supplier-balances")]
    public async Task<IActionResult> SupplierBalances([FromQuery(Name = "as_of")] string? asOf, CancellationToken ct)
    {
        var (date, error) = MonthEndMath.ParseAsOf(asOf, Today);
        if (error != null) return BadRequest(ApiResponse<SupplierBalancesResponse>.Fail(error));
        try
        {
            var rows = await _reader.ReadSupplierBalancesAsync(date!.Value, ct);
            return Ok(ApiResponse<SupplierBalancesResponse>.Ok(new SupplierBalancesResponse { AsOf = date.Value, Rows = rows }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Month-end supplier balances failed (as_of={AsOf})", date);
            return StatusCode(500, ApiResponse<SupplierBalancesResponse>.Fail(ex.Message));
        }
    }

    /// <summary>GET /api/month-end/grni?as_of= — goods received at the date and not yet invoiced at the date.</summary>
    [HttpGet("grni")]
    public async Task<IActionResult> Grni([FromQuery(Name = "as_of")] string? asOf, CancellationToken ct)
    {
        var (date, error) = MonthEndMath.ParseAsOf(asOf, Today);
        if (error != null) return BadRequest(ApiResponse<GrniResponse>.Fail(error));
        try
        {
            var rows = await _reader.ReadGrniAsync(date!.Value, ct);
            return Ok(ApiResponse<GrniResponse>.Ok(new GrniResponse
            {
                AsOf = date.Value,
                TotalLc = Math.Round(rows.Sum(r => r.OpenValueLc), 2),
                Rows = rows,
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Month-end GRNI failed (as_of={AsOf})", date);
            return StatusCode(500, ApiResponse<GrniResponse>.Fail(ex.Message));
        }
    }

    /// <summary>GET /api/month-end/inventory-valuation?as_of= — SAP's perpetual inventory value at the date (OINM), by warehouse.</summary>
    [HttpGet("inventory-valuation")]
    public async Task<IActionResult> InventoryValuation([FromQuery(Name = "as_of")] string? asOf, CancellationToken ct)
    {
        var (date, error) = MonthEndMath.ParseAsOf(asOf, Today);
        if (error != null) return BadRequest(ApiResponse<InventoryValuationResponse>.Fail(error));
        try
        {
            var rows = await _reader.ReadInventoryValuationAsync(date!.Value, ct);
            return Ok(ApiResponse<InventoryValuationResponse>.Ok(new InventoryValuationResponse
            {
                AsOf = date.Value,
                TotalLc = Math.Round(rows.Sum(r => r.ValueLc), 2),
                ByWarehouse = rows,
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Month-end inventory valuation failed (as_of={AsOf})", date);
            return StatusCode(500, ApiResponse<InventoryValuationResponse>.Fail(ex.Message));
        }
    }

    /// <summary>GET /api/month-end/landed-cost-credits?from=&amp;to= — credits per landed cost code and the documents of the period.</summary>
    [HttpGet("landed-cost-credits")]
    public async Task<IActionResult> LandedCostCredits([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        var (fromDate, toDate, error) = MonthEndMath.ParsePeriod(from, to, Today);
        if (error != null) return BadRequest(ApiResponse<LandedCostCreditsResponse>.Fail(error));
        try
        {
            var codes = await _reader.ReadLandedCostCreditsAsync(fromDate, toDate, ct);
            var documents = await _reader.ReadLandedCostDocumentsAsync(fromDate, toDate, ct);
            return Ok(ApiResponse<LandedCostCreditsResponse>.Ok(new LandedCostCreditsResponse
            {
                From = fromDate, To = toDate, Codes = codes, Documents = documents,
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Month-end landed cost credits failed ({From}..{To})", fromDate, toDate);
            return StatusCode(500, ApiResponse<LandedCostCreditsResponse>.Fail(ex.Message));
        }
    }
}
