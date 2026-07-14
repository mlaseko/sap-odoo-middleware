using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Ingestion;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;
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
    private readonly IEnrichmentResultRouter _router;
    private readonly IOemFilterService _oemFilter;
    private readonly PartsItemCreationService _itemCreation;
    private readonly IAutohubSapB1Service _sap;   // Autohub company (Molas Live 2021) connection
    private readonly INeonBridgeService _bridge;   // resolve donor oitm rows for the operator swap
    private readonly IDonorSearchClient _donorSearch;   // DGX "find more candidates" (broad TecDoc pool)

    public AutohubDocumentsController(
        IStagingPartsDocumentRepository docs,
        IPartsReviewRepository review,
        PartsDocumentUploadService uploads,
        IAutoMatchService autoMatch,
        IEnrichmentService enrichment,
        IEnrichmentResultRouter router,
        IOemFilterService oemFilter,
        PartsItemCreationService itemCreation,
        IAutohubSapB1Service sap,
        INeonBridgeService bridge,
        IDonorSearchClient donorSearch)
    {
        _docs = docs;
        _review = review;
        _uploads = uploads;
        _autoMatch = autoMatch;
        _enrichment = enrichment;
        _router = router;
        _oemFilter = oemFilter;
        _itemCreation = itemCreation;
        _sap = sap;
        _bridge = bridge;
        _donorSearch = donorSearch;
    }

    private static readonly JsonSerializerOptions EnrichmentJson = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

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

    /// <summary>Delete an uploaded invoice and its staging lines (and the stored file). Does not affect SAP.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        if (doc is null) return NotFound();

        await _docs.DeleteAsync(id, ct);   // staging lines cascade

        // Best-effort file cleanup — an orphaned file is harmless and must not fail the delete.
        try
        {
            if (!string.IsNullOrWhiteSpace(doc.FilePath) && System.IO.File.Exists(doc.FilePath))
                System.IO.File.Delete(doc.FilePath);
        }
        catch { /* ignore */ }

        return Ok(new { deleted = true });
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

    /// <summary>Manual match: the operator types a SAP item code. Verified against the Autohub company.</summary>
    [HttpPost("{documentId:guid}/lines/{lineId:guid}/match")]
    public async Task<IActionResult> MatchLine(Guid documentId, Guid lineId, [FromBody] PartsMatchRequest body, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        if (string.IsNullOrWhiteSpace(body.ItemCode)) return BadRequest(new { error = "item_code is required." });
        var itemCode = body.ItemCode.Trim();

        // Verify the item exists in the Autohub SAP company before marking matched — otherwise a typo (or
        // matching before the item is created) leaves the line "matched" to a code SAP doesn't have.
        bool exists;
        try
        {
            exists = await _sap.ItemExistsAsync(itemCode);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = $"Could not verify '{itemCode}' in the Autohub SAP company (service unavailable): {ex.Message}" });
        }
        if (!exists)
            return BadRequest(new { error = $"Item '{itemCode}' does not exist in the Autohub SAP company. Use 'Create New' to create it instead." });

        await _review.SetReviewStatusAsync(lineId, "matched", itemCode, ct);
        return Ok(await _review.GetByIdAsync(lineId, ct));
    }

    /// <summary>Re-run the Tier-1/2 auto-matcher for a SINGLE line (e.g. a 'needs manual' line), applying
    /// the decision. Lets the operator retry auto-match on one row without touching the rest.</summary>
    [HttpPost("{documentId:guid}/lines/{lineId:guid}/auto-match")]
    public async Task<IActionResult> AutoMatchLine(Guid documentId, Guid lineId, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        var line = await _review.GetByIdAsync(lineId, ct);
        if (line is null) return NotFound();

        var doc = await _docs.GetByIdAsync(documentId, ct);   // for the document-supplier brand fallback
        var candidate = new PartsLineMatchCandidate(
            line.Id, line.DocumentId, line.OemNumbers, line.SupplierArticleNumber, line.IsPromotional,
            line.Brand, doc?.SupplierName);
        var decision = await _autoMatch.DecideAsync(candidate, ct);

        switch (decision.Status)
        {
            case "matched":
                await _review.SetReviewStatusAsync(lineId, "matched", decision.ItemCode, ct);
                break;
            case "skip":
                await _review.SetReviewStatusAsync(lineId, "skip", null, ct);
                break;
            case "needs_confirmation":
                var d = decision.SuggestedDonor;
                await _review.SetNeedsConfirmationAsync(lineId, d?.ItemCode, d?.OitmId, d?.SupplierName, decision.MatchStrategy, ct);
                break;
            default:   // no match — return to the pending → enrichment pipeline
                await _review.SetReviewStatusAsync(lineId, "pending", null, ct);
                break;
        }
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
            await _review.RecordEnrichmentResultAsync(lineId, null, null, null, null, false, "failed", "dgx_unreachable", "unmatched", null, ct);
            await _review.SetReviewStatusAsync(lineId, "needs_manual", null, ct);
            return StatusCode(502, new { error = "Enrichment service failed.", detail = ex.Message });
        }

        // Persist + route: failed/partial → needs_manual; donor already a SAP item → auto-match (C1);
        // otherwise ready for the operator to confirm + create (C2). The modal/review page read the
        // persisted result without re-calling DGX.
        var routing = await _router.ApplyAsync(lineId, line.Brand, line.SupplierArticleNumber, enr, ct);
        var routingLabel = routing.Routing switch
        {
            LineEnrichmentRouting.AutoMatched      => "auto_matched",
            LineEnrichmentRouting.NeedsConfirmation => "needs_confirmation",
            LineEnrichmentRouting.NeedsManual      => "needs_manual",
            _                                      => "ready",
        };
        return Ok(new
        {
            enrichment = enr,
            routing = routingLabel,
            matched_item_code = routing.MatchedItemCode,
            match_strategy = routing.MatchStrategy,
        });
    }

    /// <summary>Reject a (borrowed) enrichment: move the line to 'needs_manual' so the operator can match-by-search (Q9).</summary>
    [HttpPost("{documentId:guid}/lines/{lineId:guid}/reject")]
    public async Task<IActionResult> RejectLine(Guid documentId, Guid lineId, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        await _review.SetReviewStatusAsync(lineId, "needs_manual", null, ct);
        return Ok(await _review.GetByIdAsync(lineId, ct));
    }

    /// <summary>Reopen a skipped line back to 'needs_manual' so the operator can match/create it (undo bulk-skip).</summary>
    [HttpPost("{documentId:guid}/lines/{lineId:guid}/reopen")]
    public async Task<IActionResult> ReopenLine(Guid documentId, Guid lineId, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        await _review.SetReviewStatusAsync(lineId, "needs_manual", null, ct);
        return Ok(await _review.GetByIdAsync(lineId, ct));
    }

    /// <summary>Flag a line 'needs_manual' (operator will match / manual-create / skip it). Available from any status.</summary>
    [HttpPost("{documentId:guid}/lines/{lineId:guid}/needs-manual")]
    public async Task<IActionResult> MarkNeedsManual(Guid documentId, Guid lineId, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        await _review.SetReviewStatusAsync(lineId, "needs_manual", null, ct);
        return Ok(await _review.GetByIdAsync(lineId, ct));
    }

    /// <summary>The persisted DGX enrichment for a line (detail panel). 204 if the line was never enriched.</summary>
    [HttpGet("{documentId:guid}/lines/{lineId:guid}/enrichment")]
    public async Task<IActionResult> GetLineEnrichment(Guid documentId, Guid lineId, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        var json = await _review.GetEnrichmentPayloadAsync(lineId, ct);
        return string.IsNullOrWhiteSpace(json) ? NoContent() : Content(json, "application/json");
    }

    /// <summary>
    /// Ranked donor candidates for a borrowed line, each enriched with its local detail (parts_catalog
    /// id + item_code + true OEM cross-references) so the swap modal can show what each donor contributes.
    /// Image/specs are not included (not guaranteed to be mirrored locally). Empty list if not a borrow.
    /// </summary>
    [HttpGet("{documentId:guid}/lines/{lineId:guid}/donor-candidates")]
    public async Task<IActionResult> DonorCandidates(Guid documentId, Guid lineId, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;

        var payload = await _review.GetEnrichmentPayloadAsync(lineId, ct);
        if (string.IsNullOrWhiteSpace(payload)) return Ok(Array.Empty<object>());

        EnrichmentResponse enr;
        try { enr = JsonSerializer.Deserialize<EnrichmentResponse>(payload, EnrichmentJson)!; }
        catch { return Ok(Array.Empty<object>()); }

        var cands = enr.Audit?.BridgeCandidatesRanked;
        if (cands is null || cands.Count == 0) return Ok(Array.Empty<object>());

        var outList = new List<object>(cands.Count);
        foreach (var c in cands)
        {
            var detail = string.IsNullOrWhiteSpace(c.ArticleNumber)
                ? null
                : await _bridge.GetDonorDetailAsync(c.ArticleNumber!, c.Supplier, ct);

            // OEM split: prefer the DGX payload's list (capped, with a true total + aftermarket count);
            // fall back to the locally-derived cross-refs for lines enriched before the split landed.
            var oemList = c.OemNumbers is { Count: > 0 }
                ? (IReadOnlyList<string>)c.OemNumbers
                : (detail?.Oems ?? (IReadOnlyList<string>)Array.Empty<string>());
            var oemTotal = c.OemCount ?? oemList.Count;

            outList.Add(new
            {
                article_number            = c.ArticleNumber,
                supplier                  = c.Supplier,
                name                      = detail?.Name ?? c.Name,
                verdict                   = c.Verdict,
                score                     = c.Score,
                auto_pick_eligible        = c.AutoPickEligible,
                is_default                = c.IsDefault,
                tecdoc_article_id         = c.TecdocArticleId,
                oitm_id                   = detail?.OitmId,
                item_code                 = detail?.ItemCode,
                image_url                 = detail?.ImageUrl,
                part_component            = detail?.PartComponent,
                is_kit                    = detail?.IsKit ?? false,
                spec_count                = detail?.SpecCount ?? 0,
                compatible_vehicles_count = detail?.CompatibleVehiclesCount ?? 0,
                categories_count          = detail?.CategoriesCount ?? 0,
                oem_numbers               = oemList,
                oem_count                 = oemTotal,
                crossref_count            = c.CrossrefCount,
            });
        }
        return Ok(outList);
    }

    /// <summary>
    /// Swap the borrowed donor for a pending, borrowed line to another ranked candidate (operator override).
    /// Option-1 local re-point: resolves the chosen candidate's parts_catalog row and re-points the line's
    /// NeonOitmId to it (no DGX call), then re-runs the router so identity/supplier gating still applies.
    /// The new SAP item is still created with a freshly-minted SKU — only the enrichment SOURCE changes
    /// (the created item's ItemName then carries the chosen donor's OEM chain). Not allowed once created.
    /// </summary>
    [HttpPost("{documentId:guid}/lines/{lineId:guid}/swap-donor")]
    public async Task<IActionResult> SwapDonor(Guid documentId, Guid lineId, [FromBody] SwapDonorRequest body, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        if (string.IsNullOrWhiteSpace(body?.ArticleNumber))
            return BadRequest(new { error = "article_number is required." });

        var line = await _review.GetByIdAsync(lineId, ct);
        if (line is null) return NotFound();
        if (line.ReviewStatus is "created")
            return Conflict(new { error = "Line already created; cannot swap donor." });

        var payload = await _review.GetEnrichmentPayloadAsync(lineId, ct);
        if (string.IsNullOrWhiteSpace(payload))
            return BadRequest(new { error = "Line has no enrichment to swap." });

        EnrichmentResponse enr;
        try { enr = JsonSerializer.Deserialize<EnrichmentResponse>(payload, EnrichmentJson)!; }
        catch (Exception ex) { return BadRequest(new { error = $"Stored enrichment unreadable: {ex.Message}" }); }

        var candidates = enr.Audit?.BridgeCandidatesRanked;
        if (candidates is null || candidates.Count == 0)
            return BadRequest(new { error = "No donor candidates available for this line." });

        var chosen = candidates.FirstOrDefault(c =>
            string.Equals(c.ArticleNumber?.Trim(), body.ArticleNumber.Trim(), StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
            return BadRequest(new { error = $"Article '{body.ArticleNumber}' is not among this line's donor candidates." });

        // Resolve the chosen donor's local parts_catalog row (Option 1 — no DGX round-trip).
        var donorId = await _bridge.FindOitmIdByArticleSupplierAsync(chosen.ArticleNumber!, chosen.Supplier ?? body.Supplier, ct);
        if (donorId is null)
            return UnprocessableEntity(new { error = $"Donor '{chosen.ArticleNumber}' not found in parts_catalog." });

        // Re-point the borrow: keep group/prefix (brand-driven), refresh the description + selection metadata,
        // mark the chosen candidate as the default. The router re-serializes this into EnrichmentPayloadJson.
        var newSelection = (enr.Audit?.Selection ?? new DonorSelection()) with
        {
            SelectedComponentVerdict = chosen.Verdict,
            NeedsReview              = !string.Equals(chosen.Verdict, "MATCH", StringComparison.OrdinalIgnoreCase),
            SelectedName             = chosen.Name,
            SelectedSupplier         = chosen.Supplier,
        };
        var reranked = candidates.Select(c => c with { IsDefault = ReferenceEquals(c, chosen) }).ToList();
        var swapped = enr with
        {
            NeonOitmId   = (int)donorId.Value,
            ItemData     = enr.ItemData is null ? null : enr.ItemData with { PrimaryDescription = chosen.Name ?? enr.ItemData.PrimaryDescription },
            BorrowedFrom = new BorrowedFrom { ArticleNumber = chosen.ArticleNumber, SupplierName = chosen.Supplier },
            Audit        = new EnrichmentAudit { Selection = newSelection, BridgeCandidatesRanked = reranked },
        };

        var routing = await _router.ApplyAsync(lineId, line.Brand, line.SupplierArticleNumber, swapped, ct);
        await _review.MarkEditedAsync(lineId, CurrentUser, ct);

        var reviewStatus = routing.Routing switch
        {
            LineEnrichmentRouting.AutoMatched      => "matched",
            LineEnrichmentRouting.NeedsConfirmation => "needs_confirmation",
            LineEnrichmentRouting.NeedsManual      => "needs_manual",
            _                                      => "pending",
        };

        return Ok(new
        {
            swapped_to     = chosen.ArticleNumber,
            supplier       = chosen.Supplier,
            verdict        = chosen.Verdict,
            needs_review   = newSelection.NeedsReview,
            review_status  = reviewStatus,
            match_strategy = routing.MatchStrategy,
        });
    }

    /// <summary>
    /// "Find more candidates" — the broad DGX/TecDoc pool for this line's OEMs (on operator click only,
    /// never on modal open). Returns { available, error?, candidates[] }; a 501/rapidapi-off DGX yields
    /// available=false so the UI keeps the local list.
    /// </summary>
    [HttpPost("{documentId:guid}/lines/{lineId:guid}/donor-candidates/search")]
    public async Task<IActionResult> SearchDonorCandidates(Guid documentId, Guid lineId, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        var line = await _review.GetByIdAsync(lineId, ct);
        if (line is null) return NotFound();

        var oems = _oemFilter.Filter(line.OemNumbers, line.SupplierArticleNumber, line.Brand).CleanOems;
        if (oems.Count == 0)
            return Ok(new { available = false, error = "no_oems", candidates = Array.Empty<object>() });

        DonorSearchResponse res;
        try { res = await _donorSearch.CandidatesForLineAsync(oems, line.Description, ct); }
        catch (Exception ex) { return Ok(new { available = false, error = ex.Message, candidates = Array.Empty<object>() }); }

        if (!res.Found || res.Candidates is null)
            return Ok(new { available = false, error = res.Error ?? "no_results", candidates = Array.Empty<object>() });

        var cands = res.Candidates.Select(c => new
        {
            article_number     = c.ArticleNumber,
            supplier           = c.Supplier,
            name               = c.Name,
            verdict            = c.Verdict,
            score              = c.Score,
            auto_pick_eligible = c.AutoPickEligible ?? !string.Equals(c.Verdict, "DIFFERENT", StringComparison.OrdinalIgnoreCase),
            is_default         = c.IsDefault,
            tecdoc_article_id  = c.TecdocArticleId,
            supplier_id        = c.SupplierId,
            matched_oem        = c.MatchedOem,
            shared_oem_count   = c.SharedOemCount,
            crossref_count     = c.CrossrefCount,
            image_url          = c.ImageUrl,
            source             = c.Source ?? "rapidapi_tecdoc",
        });
        return Ok(new { available = true, error = (string?)null, candidates = cands });
    }

    /// <summary>Lazy full OEM list for one API candidate (DGX /article_oems), on operator expand.</summary>
    [HttpPost("{documentId:guid}/lines/{lineId:guid}/donor-candidates/oems")]
    public async Task<IActionResult> DonorCandidateOems(Guid documentId, Guid lineId, [FromBody] ArticleOemsBody body, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        if (body is null || body.TecdocArticleId <= 0)
            return BadRequest(new { error = "tecdoc_article_id is required." });

        ArticleOemsResponse res;
        try { res = await _donorSearch.ArticleOemsAsync(body.TecdocArticleId, ct); }
        catch (Exception ex) { return Ok(new { available = false, error = ex.Message, oem_numbers = Array.Empty<string>() }); }

        if (!res.Found)
            return Ok(new { available = false, error = res.Error ?? "not_found", oem_numbers = Array.Empty<string>() });

        return Ok(new
        {
            available      = true,
            oem_numbers    = res.OemNumbers ?? new List<string>(),
            oem_count      = res.OemCount ?? (res.OemNumbers?.Count ?? 0),
            crossref_count = res.CrossrefCount,
        });
    }

    /// <summary>
    /// Swap the borrow to a candidate from the broad DGX pool (not in the local list). Option 2: the DGX
    /// materializes the chosen TecDoc article into oitm (item_code NULL) and returns a neon_oitm_id, which
    /// we re-point the line's borrow to — then re-run the router (identity gating; still a fresh SKU).
    /// </summary>
    [HttpPost("{documentId:guid}/lines/{lineId:guid}/swap-donor-api")]
    public async Task<IActionResult> SwapDonorApi(Guid documentId, Guid lineId, [FromBody] SwapDonorApiRequest body, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        if (body is null || (body.TecdocArticleId is null && string.IsNullOrWhiteSpace(body.ArticleNumber)))
            return BadRequest(new { error = "tecdoc_article_id or article_number is required." });

        var line = await _review.GetByIdAsync(lineId, ct);
        if (line is null) return NotFound();
        if (line.ReviewStatus is "created")
            return Conflict(new { error = "Line already created; cannot swap donor." });

        var payload = await _review.GetEnrichmentPayloadAsync(lineId, ct);
        if (string.IsNullOrWhiteSpace(payload))
            return BadRequest(new { error = "Line has no enrichment to swap." });

        EnrichmentResponse enr;
        try { enr = JsonSerializer.Deserialize<EnrichmentResponse>(payload, EnrichmentJson)!; }
        catch (Exception ex) { return BadRequest(new { error = $"Stored enrichment unreadable: {ex.Message}" }); }

        var lineOems = _oemFilter.Filter(line.OemNumbers, line.SupplierArticleNumber, line.Brand).CleanOems;

        MaterializeResponse mat;
        try
        {
            mat = await _donorSearch.MaterializeCandidateAsync(new MaterializeRequest(
                body.TecdocArticleId, body.ArticleNumber, body.Supplier, body.SupplierId, line.Description, lineOems), ct);
        }
        catch (Exception ex) { return StatusCode(502, new { error = $"Materialize failed: {ex.Message}" }); }

        if (!mat.Found || mat.NeonOitmId is null)
            return UnprocessableEntity(new { error = mat.Error ?? "Could not add that donor (materialize returned no id)." });

        var article  = mat.ArticleNumber ?? body.ArticleNumber;
        var supplier = mat.Supplier ?? body.Supplier;
        var name     = mat.Name ?? body.Name;
        var verdict  = body.Verdict;

        var newSelection = (enr.Audit?.Selection ?? new DonorSelection()) with
        {
            SelectedComponentVerdict = verdict,
            NeedsReview              = !string.Equals(verdict, "MATCH", StringComparison.OrdinalIgnoreCase),
            SelectedName             = name,
            SelectedSupplier         = supplier,
        };
        var swapped = enr with
        {
            NeonOitmId   = (int)mat.NeonOitmId.Value,
            ItemData     = enr.ItemData is null ? null : enr.ItemData with { PrimaryDescription = name ?? enr.ItemData.PrimaryDescription },
            BorrowedFrom = new BorrowedFrom { ArticleNumber = article, SupplierName = supplier },
            Audit        = (enr.Audit ?? new EnrichmentAudit()) with { Selection = newSelection },
        };

        var routing = await _router.ApplyAsync(lineId, line.Brand, line.SupplierArticleNumber, swapped, ct);
        await _review.MarkEditedAsync(lineId, CurrentUser, ct);

        var reviewStatus = routing.Routing switch
        {
            LineEnrichmentRouting.AutoMatched       => "matched",
            LineEnrichmentRouting.NeedsConfirmation => "needs_confirmation",
            LineEnrichmentRouting.NeedsManual       => "needs_manual",
            _                                       => "pending",
        };

        return Ok(new
        {
            swapped_to     = article,
            supplier,
            verdict,
            needs_review   = newSelection.NeedsReview,
            review_status  = reviewStatus,
            match_strategy = routing.MatchStrategy,
            neon_oitm_id   = mat.NeonOitmId,
        });
    }

    /// <summary>Operator edits to an extracted line before creation (qty / unit price / description).</summary>
    [HttpPatch("{documentId:guid}/lines/{lineId:guid}")]
    public async Task<IActionResult> UpdateLine(Guid documentId, Guid lineId, [FromBody] UpdateLineRequest body, CancellationToken ct)
    {
        if (await GuardLine(documentId, lineId, ct) is { } err) return err;
        if (body.Quantity is < 0 || body.UnitPriceForeign is < 0)
            return BadRequest(new { error = "Quantity and unit price must be non-negative." });
        await _review.UpdateLineFieldsAsync(lineId, body.Quantity, body.UnitPriceForeign, body.Description?.Trim(), ct);
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

        int newlyMatched = 0, needsConfirmation = 0, stillPending = 0;
        foreach (var l in pending)
        {
            var candidate = new PartsLineMatchCandidate(l.Id, l.DocumentId, l.OemNumbers, l.SupplierArticleNumber, l.IsPromotional, l.Brand, doc.SupplierName);
            var decision = await _autoMatch.DecideAsync(candidate, ct);
            switch (decision.Status)
            {
                case "matched": await _review.SetReviewStatusAsync(l.Id, "matched", decision.ItemCode, ct); newlyMatched++; break;
                case "skip":    await _review.SetReviewStatusAsync(l.Id, "skip", null, ct); break;
                case "needs_confirmation":
                    var d = decision.SuggestedDonor;
                    await _review.SetNeedsConfirmationAsync(l.Id, d?.ItemCode, d?.OitmId, d?.SupplierName, decision.MatchStrategy, ct);
                    needsConfirmation++;
                    break;
                default:        stillPending++; break;
            }
        }
        return Ok(new { totalPending = pending.Count, newlyMatched, needsConfirmation, stillPending });
    }

    [HttpPost("{documentId:guid}/bulk-mark-pending-as-create-new")]
    public async Task<IActionResult> BulkMarkPendingAsCreateNew(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();
        return Ok(new { updated = await _review.BulkSetPendingToCreateNewAsync(documentId, ct) });
    }

    /// <summary>Create items in SAP+Neon for every 'create_new' line (sequential, continues on failure).</summary>
    /// <summary>Skip every unresolved (pending / needs_manual) line in one go (review-page bulk action).</summary>
    [HttpPost("{documentId:guid}/bulk-skip-pending")]
    public async Task<IActionResult> BulkSkipPending(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();
        return Ok(new { skipped = await _review.BulkSkipPendingAsync(documentId, ct) });
    }

    /// <summary>
    /// Undo bulk-skip. Default: skipped → 'needs_manual'. With <c>?reEnrich=true</c>: reset to 'pending'
    /// with enrichment cleared so the background worker re-queries DGX and re-routes (C1/C2/needs_manual).
    /// </summary>
    [HttpPost("{documentId:guid}/bulk-reopen-skipped")]
    public async Task<IActionResult> BulkReopenSkipped(Guid documentId, [FromQuery] bool reEnrich, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();

        var count = reEnrich
            ? await _review.BulkReenrichSkippedAsync(documentId, ct)
            : await _review.BulkReopenSkippedAsync(documentId, ct);
        return Ok(new { reopened = count, reEnriched = reEnrich });
    }

    /// <summary>
    /// Re-run enrichment for the residual blockers only — lines DGX couldn't classify
    /// (EnrichmentStatus 'partial'/'unmatched'), never the resolved ('matched'/'created') ones. Use after a
    /// DGX classifier improvement to pick up only what was stuck. The background worker re-queries DGX.
    /// </summary>
    [HttpPost("{documentId:guid}/bulk-reenrich-blockers")]
    public async Task<IActionResult> BulkReenrichBlockers(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();
        return Ok(new { reEnriched = await _review.BulkReenrichBlockersAsync(documentId, ct) });
    }

    /// <summary>Confirm all unconfirmed 'create_new' lines at once, so Bulk Create stops blocking on the
    /// cross-supplier gate (review the borrowed lines first). Returns the count confirmed.</summary>
    [HttpPost("{documentId:guid}/bulk-confirm-create-new")]
    public async Task<IActionResult> BulkConfirmCreateNew(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();
        return Ok(new { confirmed = await _review.BulkConfirmCreateNewAsync(documentId, CurrentUser, ct) });
    }

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

    /// <summary>
    /// Manual create: create SAP items for the given lines using an operator-supplied item group + SKU
    /// prefix, for parts DGX enrichment could not classify (no suggested group/prefix). Bypasses the
    /// enrichment requirement; per-line is just this with a single id.
    /// </summary>
    [HttpPost("{documentId:guid}/bulk-create-manual")]
    public async Task<IActionResult> BulkCreateManual(Guid documentId, [FromBody] BulkCreateManualRequest body, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();
        if (body?.LineIds is not { Count: > 0 }) return BadRequest(new { error = "lineIds is required." });
        if (body.ItemsGroupCode <= 0) return BadRequest(new { error = "itemsGroupCode is required (a positive SAP item group)." });
        if (string.IsNullOrWhiteSpace(body.SkuPrefix)) return BadRequest(new { error = "skuPrefix is required (e.g. 'LR')." });

        var manual = new ManualItemOverride(
            body.ItemsGroupCode, body.SkuPrefix.Trim().ToUpperInvariant(), body.Description, body.FitForAuto, body.ImageUrl);
        var result = await _itemCreation.BulkCreateManualAsync(documentId, body.LineIds, manual, ct);
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

        var counts = await _review.GetStatusCountsAsync(documentId, ct);
        var blocking = counts.GetValueOrDefault("pending") + counts.GetValueOrDefault("create_failed")
            + counts.GetValueOrDefault("create_new") + counts.GetValueOrDefault("needs_manual")
            + counts.GetValueOrDefault("needs_confirmation");
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
        var awaitingEnrichment = await _review.CountAwaitingEnrichmentAsync(documentId, ct);
        var canComplete = doc.Status == "extracted"
            && counts.GetValueOrDefault("pending") == 0
            && counts.GetValueOrDefault("create_failed") == 0
            && counts.GetValueOrDefault("create_new") == 0
            && counts.GetValueOrDefault("needs_manual") == 0
            && counts.GetValueOrDefault("needs_confirmation") == 0;

        return Ok(new { totalLines = counts.Values.Sum(), byStatus = counts, awaitingEnrichment, canComplete, status = doc.Status });
    }

    // ----------------------------------------------------------------------------------------
    // Inventory analytics
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// GET /api/autohub/documents/movement-clock
    /// Runs the Movement Clock stock-classification query against the Autohub SAP B1 database.
    /// Classifies every active item by sales velocity, recency, and age (OBSOLETE, DEAD, YEARLY,
    /// QUARTERLY, MONTHLY, NEW variants) with recommended actions, holding-cost estimates, and
    /// priority scores. Read-only — does not modify SAP data.
    /// </summary>
    [HttpGet("movement-clock")]
    public async Task<IActionResult> GetMovementClock()
    {
        try
        {
            var items = await _sap.GetMovementClockAsync();
            return Ok(ApiResponse<List<MovementClockItem>>.Ok(items));
        }
        catch (Exception ex)
        {
            return StatusCode(500, ApiResponse<List<MovementClockItem>>.Fail(ex.Message));
        }
    }
}

public sealed record PartsMatchRequest(string ItemCode);
public sealed record BulkCreateManualRequest(
    List<Guid> LineIds, int ItemsGroupCode, string SkuPrefix, string? Description, string? FitForAuto, string? ImageUrl);
public sealed record CreateNewRequest(bool Confirmed);
public sealed record UpdateLineRequest(decimal? Quantity, decimal? UnitPriceForeign, string? Description);
public sealed record SwapDonorRequest(string ArticleNumber, string? Supplier);
public sealed record ArticleOemsBody(long TecdocArticleId);
public sealed record SwapDonorApiRequest(long? TecdocArticleId, string? ArticleNumber, string? Supplier, long? SupplierId, string? Name, string? Verdict);
