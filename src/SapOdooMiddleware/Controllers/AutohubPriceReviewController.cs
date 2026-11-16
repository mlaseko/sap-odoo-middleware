using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Price review for an Autohub invoice: the prices Bulk Create would write for each line still to be
/// created, the warnings on them, and the reviewer's saved decisions. Separate from
/// AutohubDocumentsController so that controller's constructor is untouched.
/// </summary>
[ApiController]
[Route("api/autohub/documents")]
public sealed class AutohubPriceReviewController : ControllerBase
{
    private readonly IPriceReviewService _review;

    public AutohubPriceReviewController(IPriceReviewService review) => _review = review;

    /// <summary>
    /// GET /api/autohub/documents/{documentId}/price-review
    /// Every line with ReviewStatus create_new / create_failed, priced exactly as Bulk Create prices
    /// it: invoice unit price less the line discount, times the document currency's forex rate,
    /// then cost (PL01), the calculated PL03 and PL05, margin, ratio/band/floor, and warning flags.
    /// Saved decisions are included and marked stale when the figures behind them changed.
    /// Read-only. 404 when the document does not exist.
    /// </summary>
    [HttpGet("{documentId:guid}/price-review")]
    [ProducesResponseType(typeof(PriceReviewDocument), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid documentId, CancellationToken ct)
    {
        var review = await _review.GetReviewAsync(documentId, ct);
        return review is null ? NotFound() : Ok(review);
    }

    /// <summary>
    /// POST /api/autohub/documents/{documentId}/price-review/preview
    /// Live recalculation while the reviewer types: for each line, the PL05 derived from the given PL03
    /// (null = the calculated PL03), the margin and the flags. Writes nothing.
    /// <code>{ "lines": [ { "line_id": "guid", "pl03": 150000 } ] }</code>
    /// </summary>
    [HttpPost("{documentId:guid}/price-review/preview")]
    [ProducesResponseType(typeof(PriceReviewPreviewResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Preview(Guid documentId, [FromBody] PreviewRequest? request, CancellationToken ct)
    {
        var lines = request?.Lines ?? new();
        if (lines.Count == 0) return BadRequest(new { error = "lines is required." });
        if (lines.Count > MaxLines) return BadRequest(new { error = $"At most {MaxLines} lines per request." });

        var result = await _review.PreviewAsync(documentId,
            lines.Select(l => new PriceReviewPreviewRequestLine(l.LineId, l.Pl03)).ToList(), ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// POST /api/autohub/documents/{documentId}/price-review/decisions
    /// Approve or hold lines. PL03 null = accept the calculated price; a different PL03 needs a reason,
    /// and PL05 is re-derived from it. Prices are recomputed server-side from the current figures.
    /// All-or-nothing: 400 with every problem when any line is invalid, nothing saved. On success
    /// returns the number saved and the refreshed review.
    /// <code>
    /// { "reviewed_by": "Mohamed",
    ///   "lines": [ { "line_id": "guid", "status": "approved", "pl03": 150000, "reason": "similar parts sell at 150k" },
    ///              { "line_id": "guid", "status": "held",     "pl03": null,   "reason": null } ] }
    /// </code>
    /// </summary>
    [HttpPost("{documentId:guid}/price-review/decisions")]
    [ProducesResponseType(typeof(PriceReviewDecideResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PriceReviewDecideResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Decide(Guid documentId, [FromBody] DecisionsRequest? request, CancellationToken ct)
    {
        var lines = request?.Lines ?? new();
        if (lines.Count > MaxLines) return BadRequest(new { error = $"At most {MaxLines} lines per request." });

        var reviewer = !string.IsNullOrWhiteSpace(request?.ReviewedBy) ? request!.ReviewedBy!.Trim()
            : HttpContext.User?.Identity?.Name is { Length: > 0 } n ? n : "operator";
        if (reviewer.Length > 100) reviewer = reviewer[..100];

        var result = await _review.DecideAsync(documentId,
            lines.Select(l => new PriceReviewDecisionRequestLine(l.LineId, l.Status ?? "", l.Pl03, l.Reason)).ToList(),
            reviewer, ct);
        if (result is null) return NotFound();
        return result.Errors.Count > 0 ? BadRequest(result) : Ok(result);
    }

    /// <summary>
    /// DELETE /api/autohub/documents/{documentId}/price-review/decisions/{lineId}
    /// Clears a line's decision so it is unreviewed again. 404 when it had none.
    /// </summary>
    [HttpDelete("{documentId:guid}/price-review/decisions/{lineId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Clear(Guid documentId, Guid lineId, CancellationToken ct) =>
        await _review.ClearDecisionAsync(documentId, lineId, ct) ? NoContent() : NotFound();

    private const int MaxLines = 500;

    public sealed class PreviewRequest
    {
        public List<PreviewRequestLine>? Lines { get; set; }
    }

    public sealed class PreviewRequestLine
    {
        public Guid LineId { get; set; }
        public decimal? Pl03 { get; set; }
    }

    public sealed class DecisionsRequest
    {
        public string? ReviewedBy { get; set; }
        public List<DecisionRequestLine>? Lines { get; set; }
    }

    public sealed class DecisionRequestLine
    {
        public Guid LineId { get; set; }
        public string? Status { get; set; }
        public decimal? Pl03 { get; set; }
        public string? Reason { get; set; }
    }
}
