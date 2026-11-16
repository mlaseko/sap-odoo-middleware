using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Issues view of an Autohub invoice: what each line needs next, grouped, with bulk actions that resolve
/// many lines at once using the same steps as the per-line buttons.
/// </summary>
[ApiController]
[Route("api/autohub/documents")]
public sealed class AutohubIssuesController : ControllerBase
{
    private readonly IInvoiceIssuesService _issues;

    public AutohubIssuesController(IInvoiceIssuesService issues) => _issues = issues;

    private string CurrentUser => HttpContext.User?.Identity?.Name is { Length: > 0 } n ? n : "operator";

    /// <summary>
    /// GET /api/autohub/documents/{documentId}/issues
    /// Every line's next action (create_failed, fix_data, confirm_match, check_part, manual,
    /// awaiting_enrichment, assign_marque, accept_marque, sku_range_full, confirm_borrow, queue, price,
    /// ready, or done: created / matched / skipped), a summary, and the open lines grouped by next action
    /// in order of importance, each group with the bulk actions it accepts. Read-only.
    /// </summary>
    [HttpGet("{documentId:guid}/issues")]
    [ProducesResponseType(typeof(InvoiceIssues), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid documentId, CancellationToken ct)
    {
        var result = await _issues.GetAsync(documentId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// POST /api/autohub/documents/{documentId}/issues/bulk
    /// Apply one action to many lines; each line succeeds or fails on its own.
    /// <code>
    /// { "action": "assign_marque", "line_ids": ["guid", "guid"], "manufacturer_code": "MB", "items_group_code": 106 }
    /// </code>
    /// Actions: skip · queue (mark to create) · confirm_and_queue (confirm the matched / borrowed part, or
    /// "create new" on a confirm-match line) · use_suggested_match · accept_marque (the DGX's resolved
    /// marque) · assign_marque (needs manufacturer_code; items_group_code optional) · retry_create (failed
    /// lines, as a background Bulk Create job honouring the price review; returns job_id).
    /// 400 for a bad request; 404 when the document does not exist.
    /// </summary>
    [HttpPost("{documentId:guid}/issues/bulk")]
    [ProducesResponseType(typeof(IssueBulkResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Bulk(Guid documentId, [FromBody] BulkRequest? body, CancellationToken ct)
    {
        if (body is null) return BadRequest(new { error = "Body is required." });
        try
        {
            var result = await _issues.ApplyAsync(documentId,
                new IssueBulkRequest(body.Action ?? "", body.LineIds ?? new List<Guid>(), body.ManufacturerCode, body.ItemsGroupCode),
                CurrentUser, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    public sealed class BulkRequest
    {
        public string? Action { get; set; }
        public List<Guid>? LineIds { get; set; }
        public string? ManufacturerCode { get; set; }
        public int? ItemsGroupCode { get; set; }
    }
}
