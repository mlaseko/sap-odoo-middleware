using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Ingestion;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Autohub (spare-parts) document ingestion API — Phase A, extraction only. Deliberately exposes
/// just upload/list/detail/status; the Phase B review/match/create endpoints live on the Lubes
/// DocumentsController and arrive for Autohub with parts-specific shapes in Autohub Phase B.
/// Tenant is resolved from the /api/autohub prefix by TenantResolutionMiddleware.
/// </summary>
[ApiController]
[Route("api/autohub/documents")]
public class AutohubDocumentsController : ControllerBase
{
    private readonly IStagingPartsDocumentRepository _docs;
    private readonly IStagingPartsLineRepository _lines;
    private readonly PartsDocumentUploadService _uploads;

    public AutohubDocumentsController(
        IStagingPartsDocumentRepository docs,
        IStagingPartsLineRepository lines,
        PartsDocumentUploadService uploads)
    {
        _docs = docs;
        _lines = lines;
        _uploads = uploads;
    }

    /// <summary>Upload one spare-parts invoice PDF. Triggers asynchronous extraction.</summary>
    [HttpPost("upload")]
    [RequestSizeLimit(50 * 1024 * 1024)]   // 50 MB
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        var result = await _uploads.SaveAndQueueAsync(file, ct);
        if (!result.Ok)
            return BadRequest(new { error = result.Error });

        return Ok(new
        {
            documentId = result.DocumentId,
            status = result.Status,
            deduplicated = result.Deduplicated
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await _docs.ListRecentAsync(50, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        return doc is null ? NotFound() : Ok(doc);
    }

    [HttpGet("{id:guid}/lines")]
    public async Task<IActionResult> GetLines(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        if (doc is null) return NotFound();
        return Ok(await _lines.ListByDocumentAsync(id, ct));
    }

    /// <summary>Extraction-progress probe for the Detail page poller (exempt from API key).</summary>
    [HttpGet("{id:guid}/status")]
    public async Task<ActionResult<DocumentStatusResponse>> GetStatus(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        if (doc is null) return NotFound();

        return Ok(new DocumentStatusResponse(
            Id: doc.Id,
            Status: doc.Status,
            ValidationStatus: doc.ValidationStatus,
            PageCount: doc.PageCount,
            PagesProcessed: doc.PagesProcessed,
            CurrentPageStartedAtUtc: doc.CurrentPageStartedAt,
            LastPageDurationSec: doc.LastPageDurationSec,
            ServerNowUtc: DateTime.UtcNow,
            ErrorMessage: doc.ErrorMessage));
    }
}
