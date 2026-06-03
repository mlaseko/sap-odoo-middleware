using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Operator admin endpoints for the Autohub SKU counters. The counters auto-refresh from SAP on
/// startup and daily; this lets the operator force a refresh on demand (e.g. right after a bulk SAP
/// item import) and see per-prefix before/after values.
/// </summary>
[ApiController]
[Route("api/admin/sku-counters")]
public sealed class AdminSkuCountersController : ControllerBase
{
    private readonly ISapSkuCounterRefreshService _refresh;
    private readonly ILogger<AdminSkuCountersController> _logger;

    public AdminSkuCountersController(ISapSkuCounterRefreshService refresh, ILogger<AdminSkuCountersController> logger)
    {
        _refresh = refresh;
        _logger = logger;
    }

    /// <summary>POST /api/admin/sku-counters/refresh — bump every prefix to max(Neon, SAP).</summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<SkuRefreshResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<SkuRefreshResult>>>> Refresh(CancellationToken ct)
    {
        _logger.LogInformation("Manual SKU counter refresh requested via admin endpoint.");
        var results = await _refresh.RefreshAllAsync(ct);
        var bumped = results.Count(r => r.NeonNew > r.NeonWas);
        return Ok(ApiResponse<IReadOnlyList<SkuRefreshResult>>.Ok(
            results,
            new Dictionary<string, object> { ["prefix_count"] = results.Count, ["bumped"] = bumped }));
    }
}
