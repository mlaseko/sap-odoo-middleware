using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Areas.Autohub.Pages.Documents;

public class DetailModel : PageModel
{
    private readonly IStagingPartsDocumentRepository _docs;
    private readonly IStagingPartsLineRepository _lines;

    public DetailModel(IStagingPartsDocumentRepository docs, IStagingPartsLineRepository lines)
    {
        _docs = docs;
        _lines = lines;
    }

    public StagingPartsDocumentRow Doc { get; private set; } = default!;
    public IReadOnlyList<StagingPartsLineRow> Lines { get; private set; } = Array.Empty<StagingPartsLineRow>();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        if (doc is null) return NotFound();

        Doc = doc;
        Lines = await _lines.ListByDocumentAsync(id, ct);
        ViewData["Title"] = doc.InvoiceNumber ?? doc.OriginalFilename;
        return Page();
    }

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
