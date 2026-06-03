using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Areas.Autohub.Pages.Documents;

public class DetailModel : PageModel
{
    private readonly IStagingPartsDocumentRepository _docs;
    private readonly IPartsReviewRepository _review;

    public DetailModel(IStagingPartsDocumentRepository docs, IPartsReviewRepository review)
    {
        _docs = docs;
        _review = review;
    }

    public StagingPartsDocumentRow Doc { get; private set; } = default!;
    public IReadOnlyList<PartsReviewLineRow> Lines { get; private set; } = Array.Empty<PartsReviewLineRow>();

    /// <summary>Review UI shows once extracted (and stays visible, read-only, after review).</summary>
    public bool ShowReview => Doc.Status is "extracted" or "reviewed";
    public bool IsReviewed => Doc.Status == "reviewed";
    public bool IsEditable => Doc.Status == "extracted";

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        if (doc is null) return NotFound();

        Doc = doc;
        Lines = await _review.ListByDocumentAsync(id, ct);
        ViewData["Title"] = doc.InvoiceNumber ?? doc.OriginalFilename;
        return Page();
    }

    public int Count(string status) => Lines.Count(l => l.ReviewStatus == status);

    public static (string Css, string Text) LinePill(string status) => status switch
    {
        "matched"       => ("pill-green", "matched"),
        "created"       => ("pill-green", "created"),
        "create_new"    => ("pill-blue", "create new"),
        "skip"          => ("pill-grey", "skip"),
        "create_failed" => ("pill-red", "create failed"),
        _               => ("pill-yellow", "pending"),
    };

    public (string Css, string Text) ValidationBadge()
    {
        if (Doc.Status == "failed") return ("badge-red", "failed");
        return Doc.ValidationStatus switch
        {
            "ok"                     => ("badge-green", "ok"),
            "totals_mismatch"        => ("badge-yellow", "totals mismatch"),
            "missing_currency"       => ("badge-yellow", "missing currency"),
            "missing_supplier"       => ("badge-yellow", "missing supplier"),
            "missing_invoice_number" => ("badge-yellow", "missing invoice #"),
            "no_lines"               => ("badge-grey", "no lines"),
            null                     => ("badge-grey", Doc.Status),
            _                        => ("badge-grey", Doc.ValidationStatus!)
        };
    }

    /// <summary>Currency symbol/prefix for amount display.</summary>
    public string Sym() => (Doc.Currency ?? "").ToUpperInvariant() switch
    {
        "USD" => "$",
        "EUR" => "€",
        "GBP" => "£",
        "AED" => "AED ",
        ""    => "",
        _     => Doc.Currency + " "
    };
}
