using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Async Autohub Bulk Create: start a background job (survives the proxy/client request timeout) and poll
/// it for progress/result. Separate from AutohubDocumentsController so its constructor is untouched.
/// </summary>
[ApiController]
[Route("api/autohub/documents")]
public sealed class AutohubBulkCreateController : ControllerBase
{
    private readonly IStagingPartsDocumentRepository _docs;
    private readonly AutohubBulkCreateJobService _jobs;

    public AutohubBulkCreateController(IStagingPartsDocumentRepository docs, AutohubBulkCreateJobService jobs)
    {
        _docs = docs;
        _jobs = jobs;
    }

    /// <summary>Start (or return the already-running) async Bulk Create for the document. Returns a job id.</summary>
    [HttpPost("{documentId:guid}/bulk-create-async")]
    public async Task<IActionResult> Start(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();
        var job = _jobs.Start(documentId);
        return Ok(new { jobId = job.JobId, status = job.Status });
    }

    /// <summary>Poll an async Bulk Create job's progress/result.</summary>
    [HttpGet("{documentId:guid}/bulk-create-job/{jobId:guid}")]
    public IActionResult JobStatus(Guid documentId, Guid jobId)
    {
        var job = _jobs.Get(jobId);
        if (job is null || job.DocumentId != documentId) return NotFound();
        return Ok(new
        {
            status = job.Status,
            attempted = job.Attempted,
            created = job.Created,
            needsConfirmation = job.NeedsConfirmation,
            failed = job.Failed,
            error = job.Error,
            failures = job.Failures.Select(f => new { lineId = f.LineId, articleNumber = f.ArticleNumber, error = f.Error }),
        });
    }
}
