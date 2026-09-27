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

    /// <summary>
    /// Start (or return the already-running) async Bulk Create for the document. Returns a job id.
    /// The body is OPTIONAL (legacy callers send none → every eligible line, engine pricing).
    /// The price review gate posts the reviewed lines it wants created, each with an optional
    /// price decision:
    /// <code>
    /// { "requested_by": "gate",
    ///   "lines": [ { "line_id": "guid",
    ///                "unit_price_override": 21.50,     // corrected invoice unit price (document currency)
    ///                "cif_override": 58000,            // OR corrected landed cost (TZS, pre-markup) — not both
    ///                "pl03_override": 150000,          // reviewer-set selling price, applied verbatim
    ///                "override_reason": "market price" } ] }
    /// </code>
    /// Only the listed lines are attempted; an id that is not eligible fails that line only.
    /// Every override is recorded in pricing_overrides. Same job-polling contract as before.
    /// </summary>
    [HttpPost("{documentId:guid}/bulk-create-async")]
    public async Task<IActionResult> Start(
        Guid documentId,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)]
        BulkCreateStartRequest? request,
        CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();

        List<BulkCreateLineSelection>? selections = null;
        if (request?.Lines is { Count: > 0 } lines)
        {
            var errors = new List<string>();
            if (lines.Count > 500)
                errors.Add("At most 500 lines per run.");
            if (lines.Select(l => l.LineId).Distinct().Count() != lines.Count)
                errors.Add("Duplicate line_id in the list.");
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (l.LineId == Guid.Empty)
                    errors.Add($"lines[{i}]: line_id is required.");
                if (l.UnitPriceOverride is not null && l.CifOverride is not null)
                    errors.Add($"lines[{i}]: send unit_price_override OR cif_override, not both.");
                if (l.UnitPriceOverride is <= 0m)
                    errors.Add($"lines[{i}]: unit_price_override must be > 0.");
                if (l.CifOverride is <= 0m)
                    errors.Add($"lines[{i}]: cif_override must be > 0 (TZS).");
                if (l.Pl03Override is <= 0m)
                    errors.Add($"lines[{i}]: pl03_override must be > 0 (TZS).");
            }
            if (errors.Count > 0) return BadRequest(new { errors });

            selections = lines.Select(l => new BulkCreateLineSelection(
                l.LineId,
                l.UnitPriceOverride is null && l.CifOverride is null && l.Pl03Override is null
                    ? null
                    : new PartsLineOverride(
                        l.UnitPriceOverride, l.CifOverride, l.Pl03Override,
                        l.OverrideReason, request.RequestedBy))).ToList();
        }

        var job = _jobs.Start(documentId, selections);
        return Ok(new { jobId = job.JobId, status = job.Status });
    }

    /// <summary>Optional body of POST bulk-create-async (the price review gate's reviewed lines).</summary>
    public sealed class BulkCreateStartRequest
    {
        public List<BulkCreateLineRequest>? Lines { get; set; }
        public string? RequestedBy { get; set; }
    }

    public sealed class BulkCreateLineRequest
    {
        public Guid LineId { get; set; }
        /// <summary>Corrected invoice unit price, DOCUMENT currency (replaces the extracted price before forex).</summary>
        public decimal? UnitPriceOverride { get; set; }
        /// <summary>Corrected landed cost, TZS pre-markup (skips forex). Mutually exclusive with unit_price_override.</summary>
        public decimal? CifOverride { get; set; }
        /// <summary>Reviewer-set PL03, applied verbatim; PL05 re-derived by the engine's wholesale rule.</summary>
        public decimal? Pl03Override { get; set; }
        public string? OverrideReason { get; set; }
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
            held = job.Held,
            failed = job.Failed,
            error = job.Error,
            failures = job.Failures.Select(f => new { lineId = f.LineId, articleNumber = f.ArticleNumber, error = f.Error }),
        });
    }
}
