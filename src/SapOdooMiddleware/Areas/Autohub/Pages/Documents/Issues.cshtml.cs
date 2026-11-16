using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Areas.Autohub.Pages.Documents;

/// <summary>
/// Issues tab of an Autohub invoice: the lines that need attention, grouped by what they need next, with
/// bulk and per-line actions and a resolution card per line. Data and actions come from
/// /api/autohub/documents/{id}/issues*; this model only supplies the header and whether it is editable.
/// </summary>
public class IssuesModel : PageModel
{
    private readonly IStagingPartsDocumentRepository _docs;

    public IssuesModel(IStagingPartsDocumentRepository docs) => _docs = docs;

    public StagingPartsDocumentRow Doc { get; private set; } = default!;

    /// <summary>Lines can be changed until the invoice review is completed.</summary>
    public bool IsEditable => Doc.Status == "extracted";

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        if (doc is null) return NotFound();
        Doc = doc;
        ViewData["Title"] = "Issues · " + (doc.InvoiceNumber ?? doc.OriginalFilename);
        return Page();
    }
}
