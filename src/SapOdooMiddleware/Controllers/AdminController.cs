using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Integrations.Classifier;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>Operational admin endpoints (API-key protected).</summary>
[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly ICategoryTaxonomy _taxonomy;

    public AdminController(ICategoryTaxonomy taxonomy) => _taxonomy = taxonomy;

    /// <summary>
    /// Re-read the Odoo category taxonomy bundle from disk without restarting the service — so updating
    /// the taxonomy (a few times a year) doesn't require a restart.
    /// </summary>
    [HttpPost("reload-taxonomy")]
    public IActionResult ReloadTaxonomy()
    {
        _taxonomy.Reload();
        return Ok(new
        {
            loaded   = _taxonomy.IsLoaded,
            count    = _taxonomy.Count,
            loadedAt = _taxonomy.LoadedAt,
            path     = _taxonomy.FilePath,
        });
    }

    /// <summary>
    /// POST /api/admin/setup-sap-udfs?dry_run=false&amp;force=false
    /// The purchases-spec UDF routine (§4.1): creates only the missing UDFs, refuses (409) while other
    /// users are connected to the Lubes company database unless <c>force=true</c>. Run out of hours.
    /// </summary>
    [HttpPost("setup-sap-udfs")]
    public async Task<IActionResult> SetupSapUdfs(
        [FromServices] SapUdfSetupService udfSetup,
        [FromQuery(Name = "dry_run")] bool dryRun = false,
        [FromQuery] bool force = false,
        CancellationToken ct = default)
    {
        var result = await udfSetup.RunAsync(dryRun, force, ct);
        if (result.Refused)
            return StatusCode(409, ApiResponse<SapUdfSetupResult>.Ok(result));
        return Ok(ApiResponse<SapUdfSetupResult>.Ok(result));
    }

    /// <summary>
    /// The loaded Odoo categories — used by the review UI's "pick category" picker. Empty when no bundle
    /// is loaded (fail-open), in which case the UI should fall back to free-text entry.
    /// </summary>
    [HttpGet("taxonomy")]
    public IActionResult GetTaxonomy()
    {
        return Ok(new
        {
            loaded   = _taxonomy.IsLoaded,
            count    = _taxonomy.Count,
            loadedAt = _taxonomy.LoadedAt,
            categories = _taxonomy.All().Select(c => new
            {
                externalId = c.ExternalId,
                label      = c.FullPath ?? c.Name ?? c.ExternalId,
            }),
        });
    }
}
