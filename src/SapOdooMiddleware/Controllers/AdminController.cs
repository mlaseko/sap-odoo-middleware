using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Integrations.Classifier;

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
