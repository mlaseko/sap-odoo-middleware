using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.ItemProvisioning;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Inquiry preview of an item that is not in Odoo yet (Odoo: Sales ▸ Products, a search for a code finds nothing ▸
/// "Fetch from Liqui Moly"). Read-only: scrape (Neon cache first) + the Odoo category and SAP item group provisioning
/// would assign. No price, no SAP or NeonProducts write.
///
/// Async like /api/items, because a cold scrape plus the classifier can outlast the Cloudflare ~100s timeout:
/// POST /api/items/preview          — body { "article_number", "brand?", "classify?", "refresh?" } → 202 { job_id, status, status_url }
///                                    (classify=false: the scrape only, for the salesperson; the same request again
///                                    returns the running job, or a complete result for 24 h)
/// GET  /api/items/preview/{jobId}  — poll: status, queue_ahead, the live steps, the item as soon as it is read;
///                                    "completed" carries the result (status "found" | "not_found")
/// </summary>
[ApiController]
[Route("api/items/preview")]
public class ItemPreviewController : ControllerBase
{
    private readonly IItemPreviewJobStore _jobs;
    private readonly ILogger<ItemPreviewController> _logger;

    public ItemPreviewController(IItemPreviewJobStore jobs, ILogger<ItemPreviewController> logger)
    {
        _jobs = jobs;
        _logger = logger;
    }

    [HttpPost]
    public ActionResult Preview([FromBody] LubesItemPreviewRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ArticleNumber))
            return BadRequest(new { error = "article_number is required." });

        var jobId = _jobs.Enqueue(request with { ArticleNumber = request.ArticleNumber.Trim() });
        var statusUrl = $"/api/items/preview/{jobId}";
        _logger.LogInformation("Preview job {JobId} for article {Article} (brand {Brand}, classify {Classify}).",
            jobId, request.ArticleNumber, request.Brand ?? "any", request.Classify);
        var snapshot = _jobs.GetSnapshot(jobId);
        return Accepted(statusUrl, new { JobId = jobId, Status = snapshot?.Status ?? "queued", StatusUrl = statusUrl });
    }

    [HttpGet("{jobId}")]
    public ActionResult<ItemPreviewJobSnapshot> GetStatus(string jobId)
    {
        var snapshot = _jobs.GetSnapshot(jobId);
        if (snapshot is null)
            return NotFound(new { error = $"No preview job '{jobId}'." });
        return Ok(snapshot);
    }
}
