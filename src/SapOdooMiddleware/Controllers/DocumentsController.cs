using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Ingestion;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Vision;

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
    private readonly InvoiceAutoMatchJob _autoMatch;
    private readonly InvoiceItemCreationService _itemCreation;

    public DocumentsController(
        IStagingDocumentRepository docs,
        IStagingDocumentLineRepository lines,
        DocumentUploadService uploads,
        InvoiceAutoMatchJob autoMatch,
        InvoiceItemCreationService itemCreation)
    {
        _docs    = docs;
        _lines   = lines;
        _uploads = uploads;
        _autoMatch = autoMatch;
        _itemCreation = itemCreation;
    }

    // Audit identity: Windows auth is disabled in Development today (returns null), so fall back
    // to a stub. TODO: wire real operator identity when Windows auth is enabled.
    private string CurrentUser => HttpContext.User?.Identity?.Name is { Length: > 0 } n ? n : "operator";

    private static readonly string[] TerminalLineStatuses = { "matched", "created", "skip" };

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

    // ----------------------------------------------------------------------------------------
    // Phase B: review & bulk-create
    // ----------------------------------------------------------------------------------------

    /// <summary>Edit one line's fields. Article-number change resets match state to 'pending'.</summary>
    [HttpPatch("{documentId:guid}/lines/{lineId:guid}")]
    public async Task<IActionResult> PatchLine(Guid documentId, Guid lineId, [FromBody] LinePatchRequest body, CancellationToken ct)
    {
        var line = await _lines.GetByIdAsync(lineId, ct);
        if (line is null || line.DocumentId != documentId) return NotFound();

        // Merge provided fields onto the existing row. Convention: a null/omitted field means
        // "leave unchanged" (string fields are trimmed; blanks are treated as omitted).
        var newArticle = Blank(body.ArticleNumber) ?? line.ArticleNumber;
        var resetMatch = !string.Equals(newArticle?.Trim(), line.ArticleNumber?.Trim(), StringComparison.OrdinalIgnoreCase);

        var updated = await _lines.UpdateEditableFieldsAsync(
            lineId,
            articleNumber: newArticle,
            description:   Blank(body.Description)   ?? line.Description,
            packSize:      Blank(body.PackSize)      ?? line.PackSize,
            unit:          Blank(body.Unit)          ?? line.Unit,
            quantity:      body.Quantity             ?? line.Quantity,
            unitPrice:     body.UnitPrice            ?? line.UnitPrice,
            discountPct:   body.DiscountPct          ?? line.DiscountPct,
            lineTotal:     body.LineTotal            ?? line.LineTotal,
            commodityCode: Blank(body.CommodityCode) ?? line.CommodityCode,
            origin:        Blank(body.Origin)        ?? line.Origin,
            isPromotional: body.IsPromotional        ?? line.IsPromotional,
            editedBy: CurrentUser,
            resetMatchState: resetMatch,
            ct: ct);

        return Ok(updated);
    }

    [HttpPost("{documentId:guid}/lines/{lineId:guid}/match")]
    public async Task<IActionResult> MatchLine(Guid documentId, Guid lineId, [FromBody] MatchRequest body, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        if (string.IsNullOrWhiteSpace(body.Sku)) return BadRequest(new { error = "sku is required." });
        await _lines.SetReviewStatusAsync(lineId, "matched", body.Sku.Trim(), ct);
        return Ok(await _lines.GetByIdAsync(lineId, ct));
    }

    [HttpPost("{documentId:guid}/lines/{lineId:guid}/create-new")]
    public async Task<IActionResult> CreateNewLine(Guid documentId, Guid lineId, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        await _lines.SetReviewStatusAsync(lineId, "create_new", null, ct);
        return Ok(await _lines.GetByIdAsync(lineId, ct));
    }

    [HttpPost("{documentId:guid}/lines/{lineId:guid}/skip")]
    public async Task<IActionResult> SkipLine(Guid documentId, Guid lineId, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        await _lines.SetReviewStatusAsync(lineId, "skip", null, ct);
        return Ok(await _lines.GetByIdAsync(lineId, ct));
    }

    /// <summary>Re-run the SAP lookup for all pending lines.</summary>
    [HttpPost("{documentId:guid}/auto-match")]
    public async Task<IActionResult> AutoMatch(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();

        var before = await _lines.GetStatusCountsAsync(documentId, ct);
        var totalPending = before.GetValueOrDefault("pending");
        var (newlyMatched, stillPending) = await _autoMatch.RunAsync(documentId, ct);
        return Ok(new { totalPending, newlyMatched, stillPending });
    }

    [HttpPost("{documentId:guid}/bulk-mark-pending-as-create-new")]
    public async Task<IActionResult> BulkMarkPendingAsCreateNew(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();
        var updated = await _lines.BulkSetPendingToCreateNewAsync(documentId, ct);
        return Ok(new { updated });
    }

    /// <summary>Create items in SAP+Odoo for every 'create_new' line (sequential, continues on failure).</summary>
    [HttpPost("{documentId:guid}/bulk-create")]
    public async Task<IActionResult> BulkCreate(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();

        var result = await _itemCreation.BulkCreateAsync(documentId, ct);
        return Ok(new
        {
            attempted = result.Attempted,
            created = result.Created,
            failed = result.Failed,
            failures = result.Failures.Select(f => new { lineId = f.LineId, articleNumber = f.ArticleNumber, error = f.Error })
        });
    }

    /// <summary>Transition the document to 'reviewed'. 409 if not extracted or lines not terminal.</summary>
    [HttpPost("{documentId:guid}/complete-review")]
    public async Task<IActionResult> CompleteReview(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();
        if (doc.Status != "extracted")
            return Conflict(new { error = $"Document is '{doc.Status}', not 'extracted'." });

        var counts = await _lines.GetStatusCountsAsync(documentId, ct);
        var blocking = counts.GetValueOrDefault("pending") + counts.GetValueOrDefault("create_failed") + counts.GetValueOrDefault("create_new");
        if (blocking > 0)
            return Conflict(new { error = "All lines must be matched, created, or skipped before completing review.", counts });

        await _docs.MarkReviewedAsync(documentId, CurrentUser, ct);
        return Ok(new { status = "reviewed", reviewedBy = CurrentUser });
    }

    /// <summary>Aggregate review counts + flags for the review UI banner.</summary>
    [HttpGet("{documentId:guid}/review-summary")]
    public async Task<IActionResult> ReviewSummary(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();

        var counts = await _lines.GetStatusCountsAsync(documentId, ct);
        var totalLines = counts.Values.Sum();
        var canComplete = doc.Status == "extracted"
            && counts.GetValueOrDefault("pending") == 0
            && counts.GetValueOrDefault("create_failed") == 0
            && counts.GetValueOrDefault("create_new") == 0;

        return Ok(new
        {
            totalLines,
            byStatus = new
            {
                pending       = counts.GetValueOrDefault("pending"),
                matched       = counts.GetValueOrDefault("matched"),
                create_new    = counts.GetValueOrDefault("create_new"),
                skip          = counts.GetValueOrDefault("skip"),
                created       = counts.GetValueOrDefault("created"),
                create_failed = counts.GetValueOrDefault("create_failed")
            },
            canComplete,
            documentStatus = doc.Status,
            autoMatchedAt = doc.AutoMatchedAt,
            reviewedAt = doc.ReviewedAt,
            reviewedBy = doc.ReviewedBy
        });
    }

    // Returns a NotFound result if the line is missing or belongs to another document; else null.
    private async Task<IActionResult?> GuardLine(Guid documentId, Guid lineId, CancellationToken ct)
    {
        var line = await _lines.GetByIdAsync(lineId, ct);
        return (line is null || line.DocumentId != documentId) ? NotFound() : null;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>PATCH body — every field optional; a null/omitted field means "leave unchanged".
/// Numeric fields use the tolerant converter (US/EU/percent strings) as in Phase A.</summary>
public record LinePatchRequest
{
    public string? ArticleNumber { get; init; }
    public string? Description   { get; init; }
    public string? PackSize      { get; init; }
    public string? Unit          { get; init; }

    [JsonConverter(typeof(ResilientNullableDecimalConverter))] public decimal? Quantity    { get; init; }
    [JsonConverter(typeof(ResilientNullableDecimalConverter))] public decimal? UnitPrice   { get; init; }
    [JsonConverter(typeof(ResilientNullableDecimalConverter))] public decimal? DiscountPct { get; init; }
    [JsonConverter(typeof(ResilientNullableDecimalConverter))] public decimal? LineTotal   { get; init; }

    public string? CommodityCode { get; init; }
    public string? Origin        { get; init; }
    public bool?   IsPromotional { get; init; }
}

public record MatchRequest(string? Sku);

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
