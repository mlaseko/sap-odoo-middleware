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
}
