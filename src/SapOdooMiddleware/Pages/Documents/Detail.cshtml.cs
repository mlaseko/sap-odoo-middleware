using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Pages.Documents;

public class DetailModel : PageModel
{
    private readonly IStagingDocumentRepository _docs;
    private readonly IStagingDocumentLineRepository _lines;

    public DetailModel(IStagingDocumentRepository docs, IStagingDocumentLineRepository lines)
    {
        _docs  = docs;
        _lines = lines;
    }

    public StagingDocumentRow Doc { get; private set; } = default!;
    public IReadOnlyList<StagingDocumentLineRow> Lines { get; private set; } = Array.Empty<StagingDocumentLineRow>();

    public decimal SumLineTotals => Lines.Sum(l => l.LineTotal ?? 0m);
    public decimal ComputedTotal => SumLineTotals + (Doc.Freight ?? 0m);

    /// <summary>Review section is shown for extracted (editable) and reviewed (read-only) docs.</summary>
    public bool ShowReview => Doc.Status is "extracted" or "reviewed";
    public bool IsReviewed => Doc.Status == "reviewed";
    public bool IsEditable => Doc.Status == "extracted";

    /// <summary>CSS class + label for a line review-status pill.</summary>
    public static (string Css, string Text) LinePill(string status) => status switch
    {
        "matched"       => ("pill-green",  "matched"),
        "create_new"    => ("pill-blue",   "create new"),
        "skip"          => ("pill-grey",   "skip"),
        "pending"       => ("pill-yellow", "pending"),
        "created"       => ("pill-green",  "created"),
        "create_failed" => ("pill-red",    "create failed"),
        _               => ("pill-grey",   status)
    };

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        if (doc is null) return NotFound();

        Doc = doc;
        Lines = await _lines.ListByDocumentAsync(id, ct);

        // No meta-refresh here — the page polls /api/documents/{id}/status via JS and
        // does a single soft reload when extraction reaches a terminal state.
        ViewData["Title"] = doc.InvoiceNumber ?? doc.OriginalFilename;
        return Page();
    }

    public (string Css, string Text) ValidationBadge()
    {
        if (Doc.Status == "failed") return ("badge-red", "failed");
        return Doc.ValidationStatus switch
        {
            "ok"              => ("badge-green", "ok"),
            "totals_mismatch" => ("badge-yellow", "totals mismatch"),
            "no_footer"       => ("badge-yellow", "no footer"),
            "no_lines"        => ("badge-grey", "no lines"),
            null              => ("badge-grey", Doc.Status),
            _                 => ("badge-grey", Doc.ValidationStatus!)
        };
    }
}
