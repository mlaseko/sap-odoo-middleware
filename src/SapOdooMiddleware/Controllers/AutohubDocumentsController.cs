using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Ingestion;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Autohub (spare-parts) document API. Phase A = upload/list/detail/status + extraction; Phase B =
/// the review/match/enrich/create endpoints below (parts-specific shapes, mirroring the Lubes
/// DocumentsController). Tenant is resolved from the /api/autohub prefix.
/// </summary>
[ApiController]
[Route("api/autohub/documents")]
public class AutohubDocumentsController : ControllerBase
{
    private readonly IStagingPartsDocumentRepository _docs;
    private readonly IPartsReviewRepository _review;
    private readonly PartsDocumentUploadService _uploads;
    private readonly IAutoMatchService _autoMatch;
    private readonly IEnrichmentService _enrichment;
    private readonly IOemFilterService _oemFilter;
    private readonly PartsItemCreationService _itemCreation;

    public AutohubDocumentsController(
        IStagingPartsDocumentRepository docs,
        IPartsReviewRepository review,
        PartsDocumentUploadService uploads,
        IAutoMatchService autoMatch,
        IEnrichmentService enrichment,
        IOemFilterService oemFilter,
        PartsItemCreationService itemCreation)
    {
        _docs = docs;
        _review = review;
        _uploads = uploads;
        _autoMatch = autoMatch;
        _enrichment = enrichment;
        _oemFilter = oemFilter;
        _itemCreation = itemCreation;
    }

    private string CurrentUser => HttpContext.User?.Identity?.Name is { Length: > 0 } n ? n : "operator";

    /// <summary>Upload one spare-parts invoice PDF. Triggers asynchronous extraction.</summary>
    [HttpPost("upload")]
    [RequestSizeLimit(50 * 1024 * 1024)]   // 50 MB
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        var result = await _uploads.SaveAndQueueAsync(file, ct);
        if (!result.Ok) return BadRequest(new { error = result.Error });
        return Ok(new { documentId = result.DocumentId, status = result.Status, deduplicated = result.Deduplicated });
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
        return Ok(await _review.ListByDocumentAsync(id, ct));
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

    // ----------------------------------------------------------------------------------------
    // Phase B: review · match · enrich · create
    // ----------------------------------------------------------------------------------------

    private async Task<IActionResult?> GuardLine(Guid documentId, Guid lineId, CancellationToken ct)
    {
        var line = await _review.GetByIdAsync(lineId, ct);
        if (line is null || line.DocumentId != documentId) return NotFound();
        return null;
    }

    [HttpPost("{documentId:guid}/lines/{lineId:guid}/match")]
    public async Task<IActionResult> MatchLine(Guid documentId, Guid lineId, [FromBody] PartsMatchRequest body, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        if (string.IsNullOrWhiteSpace(body.ItemCode)) return BadRequest(new { error = "item_code is required." });
        await _review.SetReviewStatusAsync(lineId, "matched", body.ItemCode.Trim(), ct);
        return Ok(await _review.GetByIdAsync(lineId, ct));
    }

    [HttpPost("{documentId:guid}/lines/{lineId:guid}/skip")]
    public async Task<IActionResult> SkipLine(Guid documentId, Guid lineId, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        await _review.SetReviewStatusAsync(lineId, "skip", null, ct);
        return Ok(await _review.GetByIdAsync(lineId, ct));
    }

    /// <summary>Run enrichment for one line and return the package (for the borrowed-data modal).</summary>
    [HttpPost("{documentId:guid}/lines/{lineId:guid}/enrich")]
    public async Task<IActionResult> EnrichLine(Guid documentId, Guid lineId, CancellationToken ct)
    {
        var line = await _review.GetByIdAsync(lineId, ct);
        if (line is null || line.DocumentId != documentId) return NotFound();

        var clean = _oemFilter.Filter(line.OemNumbers, line.SupplierArticleNumber, line.Brand).CleanOems;
        EnrichmentResponse enr;
        try
        {
            enr = await _enrichment.EnrichLineAsync(
                new EnrichmentInput(line.SupplierArticleNumber, clean, line.Brand, line.Description, null), ct);
        }
        catch (Exception ex)
        {
            // Hard transport failure — never silently drop the line; flag it for the operator (Q8).
            await _review.RecordEnrichmentResultAsync(lineId, null, null, null, null, false, "failed", "dgx_unreachable", null, ct);
            await _review.SetReviewStatusAsync(lineId, "needs_manual", null, ct);
            return StatusCode(502, new { error = "Enrichment service failed.", detail = ex.Message });
        }

        // Persist the full result (source, borrowed, neon_oitm_id, payload) so the modal, review page
        // and bulk-create can read it without re-calling DGX. Not yet confirmed — operator confirms.
        var status = enr.Status ?? (enr.ItemData is null ? "partial" : "success");
        await _review.RecordEnrichmentResultAsync(lineId, enr.SourceLabel,
            enr.BorrowedFrom?.ArticleNumber, enr.BorrowedFrom?.SupplierName, enr.NeonOitmId,
            enr.ConfirmationRequired, status, enr.Error?.Code, JsonSerializer.Serialize(enr), ct);

        // No usable enrichment (hard failure or partial/unmatched) → needs_manual; never auto-dropped (Q8).
        if (enr.IsFailed || enr.ItemData is null)
            await _review.SetReviewStatusAsync(lineId, "needs_manual", null, ct);

        return Ok(enr);
    }

    /// <summary>Reject a (borrowed) enrichment: move the line to 'needs_manual' so the operator can match-by-search (Q9).</summary>
    [HttpPost("{documentId:guid}/lines/{lineId:guid}/reject")]
    public async Task<IActionResult> RejectLine(Guid documentId, Guid lineId, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        await _review.SetReviewStatusAsync(lineId, "needs_manual", null, ct);
        return Ok(await _review.GetByIdAsync(lineId, ct));
    }

    /// <summary>Mark a line 'create_new'. <c>confirmed=true</c> stamps the enrichment confirmation.</summary>
    [HttpPost("{documentId:guid}/lines/{lineId:guid}/create-new")]
    public async Task<IActionResult> CreateNewLine(Guid documentId, Guid lineId, [FromBody] CreateNewRequest? body, CancellationToken ct)
    {
        var line = await _review.GetByIdAsync(lineId, ct);
        if (line is null || line.DocumentId != documentId) return NotFound();

        await _review.SetReviewStatusAsync(lineId, "create_new", null, ct);
        if (body?.Confirmed == true)
            await _review.ConfirmEnrichmentAsync(lineId, CurrentUser, ct);

        return Ok(await _review.GetByIdAsync(lineId, ct));
    }

    /// <summary>Re-run Tier-1/Tier-2 auto-match for all pending lines of this document.</summary>
    [HttpPost("{documentId:guid}/auto-match")]
    public async Task<IActionResult> AutoMatch(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();

        var lines = await _review.ListByDocumentAsync(documentId, ct);
        var pending = lines.Where(l => l.ReviewStatus == "pending").ToList();

        int newlyMatched = 0, stillPending = 0;
        foreach (var l in pending)
        {
            var candidate = new PartsLineMatchCandidate(l.Id, l.DocumentId, l.OemNumbers, l.SupplierArticleNumber, l.IsPromotional);
            var decision = await _autoMatch.DecideAsync(candidate, ct);
            switch (decision.Status)
            {
                case "matched": await _review.SetReviewStatusAsync(l.Id, "matched", decision.ItemCode, ct); newlyMatched++; break;
                case "skip":    await _review.SetReviewStatusAsync(l.Id, "skip", null, ct); break;
                default:        stillPending++; break;
            }
        }
        return Ok(new { totalPending = pending.Count, newlyMatched, stillPending });
    }

    [HttpPost("{documentId:guid}/bulk-mark-pending-as-create-new")]
    public async Task<IActionResult> BulkMarkPendingAsCreateNew(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();
        return Ok(new { updated = await _review.BulkSetPendingToCreateNewAsync(documentId, ct) });
    }

    /// <summary>Create items in SAP+Neon for every 'create_new' line (sequential, continues on failure).</summary>
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
            needsConfirmation = result.NeedsConfirmation,
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

        var counts = await _review.GetStatusCountsAsync(documentId, ct);
        var blocking = counts.GetValueOrDefault("pending") + counts.GetValueOrDefault("create_failed")
            + counts.GetValueOrDefault("create_new") + counts.GetValueOrDefault("needs_manual");
        if (blocking > 0)
            return Conflict(new { error = "All lines must be matched, created, or skipped before completing review.", counts });

        await _docs.MarkReviewedAsync(documentId, CurrentUser, ct);
        return Ok(new { status = "reviewed", reviewedBy = CurrentUser });
    }

    [HttpGet("{documentId:guid}/review-summary")]
    public async Task<IActionResult> ReviewSummary(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();

        var counts = await _review.GetStatusCountsAsync(documentId, ct);
        var canComplete = doc.Status == "extracted"
            && counts.GetValueOrDefault("pending") == 0
            && counts.GetValueOrDefault("create_failed") == 0
            && counts.GetValueOrDefault("create_new") == 0
            && counts.GetValueOrDefault("needs_manual") == 0;

        return Ok(new { totalLines = counts.Values.Sum(), byStatus = counts, canComplete, status = doc.Status });
    }
}

public sealed record PartsMatchRequest(string ItemCode);
public sealed record CreateNewRequest(bool Confirmed);
