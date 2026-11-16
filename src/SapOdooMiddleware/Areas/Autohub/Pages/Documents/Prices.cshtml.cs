using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Areas.Autohub.Pages.Documents;

/// <summary>
/// Price review window for one Autohub invoice: the prices the tool calculated for every line still to
/// be created, an editable PL03 per line (PL05 follows), approve / hold, and the related parts with
/// their prices. All data comes from /api/autohub/documents/{id}/price-review*; this model only
/// supplies the header and whether the invoice can still be edited.
/// </summary>
public class PricesModel : PageModel
{
    private readonly IStagingPartsDocumentRepository _docs;

    public PricesModel(IStagingPartsDocumentRepository docs) => _docs = docs;

    public StagingPartsDocumentRow Doc { get; private set; } = default!;

    /// <summary>Decisions can be changed until the invoice review is completed.</summary>
    public bool IsEditable => Doc.Status == "extracted";

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        if (doc is null) return NotFound();
        Doc = doc;
        ViewData["Title"] = "Price review · " + (doc.InvoiceNumber ?? doc.OriginalFilename);
        return Page();
    }
}
