using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.ItemProvisioning;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Item Provisioning endpoints. Creates one Liqui Moly item master end-to-end
/// (SAP B1 is the system of record; Neon rows feed the Neon → Odoo automation).
///
/// Provisioning runs asynchronously: the cold scrape + classifier calls can take well
/// over a minute, which exceeds the Cloudflare proxy timeout. So POST queues the work
/// and returns 202 immediately; the caller polls GET /api/items/{jobId} until the job
/// reaches "completed" or "failed".
///
/// POST /api/items            — queue provisioning; body (snake_case):
///                              { "article_number", "eur_cost", "eur_tzs_rate_override?", "dry_run?" }
/// GET  /api/items/{jobId}    — poll job status + result
/// </summary>
[ApiController]
[Route("api/items")]
public class ItemsController : ControllerBase
{
    private readonly IProvisioningJobStore _jobs;
    private readonly ILogger<ItemsController> _logger;

    public ItemsController(IProvisioningJobStore jobs, ILogger<ItemsController> logger)
    {
        _jobs = jobs;
        _logger = logger;
    }

    /// <summary>Queue provisioning of one Lubes item; returns 202 + a job id to poll.</summary>
    [HttpPost]
    public ActionResult Provision([FromBody] LubesProvisioningRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ArticleNumber))
            return BadRequest(new { error = "article_number is required." });
        if (request.EurCost <= 0m)
            return BadRequest(new { error = "eur_cost must be greater than 0." });

        var jobId = _jobs.Enqueue(request);
        var statusUrl = $"/api/items/{jobId}";

        _logger.LogInformation(
            "Queued provisioning job {JobId} for article {Article} (DryRun={DryRun}).",
            jobId, request.ArticleNumber, request.DryRun);

        return Accepted(statusUrl, new { JobId = jobId, Status = "queued", StatusUrl = statusUrl });
    }

    /// <summary>Poll a provisioning job's status and (once finished) its result.</summary>
    [HttpGet("{jobId}")]
    public ActionResult<ProvisioningJobSnapshot> GetStatus(string jobId)
    {
        var snapshot = _jobs.GetSnapshot(jobId);
        if (snapshot is null)
            return NotFound(new { error = $"No provisioning job '{jobId}'." });
        return Ok(snapshot);
    }
}
