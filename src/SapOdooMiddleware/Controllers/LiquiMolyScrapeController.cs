using Microsoft.AspNetCore.Mvc;
using MolasLubes.Infrastructure.Integrations.LiquiMoly;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Scrape-and-upsert endpoint for Liqui Moly product data.
///
/// POST /api/liquimoly/scrape/{articleNumber} — scrape one article and upsert it
/// into NeonLiquiMolyProducts.
/// </summary>
[ApiController]
[Route("api/liquimoly")]
public class LiquiMolyScrapeController : ControllerBase
{
    private readonly LiquiMolyProductScraperService _scraper;
    private readonly INeonLiquiMolyRepository _repo;
    private readonly ILogger<LiquiMolyScrapeController> _logger;

    public LiquiMolyScrapeController(
        LiquiMolyProductScraperService scraper,
        INeonLiquiMolyRepository repo,
        ILogger<LiquiMolyScrapeController> logger)
    {
        _scraper = scraper;
        _repo    = repo;
        _logger  = logger;
    }

    /// <summary>Scrape one Liqui Moly article and upsert it into NeonLiquiMolyProducts.</summary>
    [HttpPost("scrape/{articleNumber}")]
    public async Task<ActionResult<LiquiMolyProductDto>> Scrape(string articleNumber, CancellationToken ct)
    {
        _logger.LogInformation("Liqui Moly scrape requested for article {ArticleNumber}", articleNumber);

        var scraped = await _scraper.ScrapeByArticleNumbersAsync(new[] { articleNumber }, ct);
        if (scraped is null || scraped.Count == 0)
            return NotFound(new { error = $"No data returned from Liqui Moly for {articleNumber}." });

        var dto = scraped[0];
        await _repo.UpsertAsync(dto, ct);
        return Ok(dto);
    }
}
