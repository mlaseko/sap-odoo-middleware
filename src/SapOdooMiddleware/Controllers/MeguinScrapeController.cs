using Microsoft.AspNetCore.Mvc;
using MolasLubes.Infrastructure.Integrations.LiquiMoly;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Scrape-and-upsert endpoint for Meguin (Liqui Moly subsidiary) product data.
///
/// POST /api/meguin/scrape/{articleNumber} — scrape one Meguin article from meguin.com and upsert it
/// into NeonLiquiMolyProducts (the shared source cache). Mirrors the Liqui Moly endpoint.
/// </summary>
[ApiController]
[Route("api/meguin")]
public class MeguinScrapeController : ControllerBase
{
    private readonly MeguinProductScraperService _scraper;
    private readonly INeonLiquiMolyRepository _repo;
    private readonly ILogger<MeguinScrapeController> _logger;

    public MeguinScrapeController(
        MeguinProductScraperService scraper,
        INeonLiquiMolyRepository repo,
        ILogger<MeguinScrapeController> logger)
    {
        _scraper = scraper;
        _repo    = repo;
        _logger  = logger;
    }

    /// <summary>Scrape one Meguin article and upsert it into NeonLiquiMolyProducts.</summary>
    [HttpPost("scrape/{articleNumber}")]
    public async Task<ActionResult<LiquiMolyProductDto>> Scrape(string articleNumber, CancellationToken ct)
    {
        _logger.LogInformation("Meguin scrape requested for article {ArticleNumber}", articleNumber);

        // The Meguin index is built in the background on startup. Fail fast while it's still warming
        // rather than blocking on the cold crawl.
        if (!_scraper.IsIndexWarm())
        {
            Response.Headers.RetryAfter = "120";
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status  = "warming",
                message = $"Meguin product index is still building. Retry {articleNumber} in 1–2 minutes."
            });
        }

        var scraped = await _scraper.ScrapeByArticleNumbersAsync(new[] { articleNumber }, ct);
        if (scraped is null || scraped.Count == 0)
            return NotFound(new { error = $"No data returned from Meguin for {articleNumber}." });

        var dto = scraped[0];
        await _repo.UpsertAsync(dto, ct);
        return Ok(dto);
    }
}
