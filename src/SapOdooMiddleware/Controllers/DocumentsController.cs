using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Ingestion;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Invoice document ingestion API (Phase A — read-only into staging). Protected by
/// ApiKeyMiddleware for curl/automation; the browser UI calls the services in-process.
///
/// POST /api/documents/upload      — upload one Liqui Moly PDF; triggers async extraction
/// GET  /api/documents             — list recent documents
/// GET  /api/documents/{id}        — one document (header/footer/validation)
/// GET  /api/documents/{id}/lines  — extracted line items for a document
/// </summary>
[ApiController]
[Route("api/documents")]
public class DocumentsController : ControllerBase
{
    private readonly IStagingDocumentRepository _docs;
    private readonly IStagingDocumentLineRepository _lines;
    private readonly DocumentUploadService _uploads;

    public DocumentsController(
        IStagingDocumentRepository docs,
        IStagingDocumentLineRepository lines,
        DocumentUploadService uploads)
    {
        _docs    = docs;
        _lines   = lines;
        _uploads = uploads;
    }

    /// <summary>Upload one Liqui Moly invoice PDF. Triggers asynchronous extraction.</summary>
    [HttpPost("upload")]
    [RequestSizeLimit(50 * 1024 * 1024)]   // 50 MB
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        var result = await _uploads.SaveAndQueueAsync(file, ct);
        if (!result.Ok)
            return BadRequest(new { error = result.Error });

        return Ok(new
        {
            document_id   = result.DocumentId,
            status        = result.Status,
            deduplicated  = result.Deduplicated
        });
    }

    [HttpGet]
    public async Task<IActionResult> ListRecent([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var docs = await _docs.ListRecentAsync(Math.Clamp(limit, 1, 200), ct);
        return Ok(docs);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        if (doc is null) return NotFound();
        return Ok(doc);
    }

    [HttpGet("{id:guid}/lines")]
    public async Task<IActionResult> GetLines(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        if (doc is null) return NotFound();
        var lines = await _lines.ListByDocumentAsync(id, ct);
        return Ok(lines);
    }

    /// <summary>
    /// Lightweight extraction-progress probe for the Detail page poller. Single PK select,
    /// no joins. Exempt from the API key in ApiKeyMiddleware (UI-facing, browser-polled).
    /// </summary>
    [HttpGet("{id:guid}/status")]
    public async Task<ActionResult<DocumentStatusResponse>> GetStatus(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        if (doc is null) return NotFound();

        return Ok(new DocumentStatusResponse(
            Id: doc.Id,
            Status: doc.Status,
            ValidationStatus: doc.ValidationStatus,
            PageCount: doc.PageCount ?? 0,
            PagesProcessed: doc.PagesProcessed,
            CurrentPageStartedAtUtc: doc.CurrentPageStartedAt,
            LastPageDurationSec: doc.LastPageDurationSec,
            ServerNowUtc: DateTime.UtcNow,
            ErrorMessage: doc.ErrorMessage));
    }
}

public record DocumentStatusResponse(
    Guid Id,
    string Status,
    string? ValidationStatus,
    int PageCount,
    int PagesProcessed,
    DateTime? CurrentPageStartedAtUtc,
    decimal? LastPageDurationSec,
    DateTime ServerNowUtc,
    string? ErrorMessage);
