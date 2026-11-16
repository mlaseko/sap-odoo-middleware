using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Areas.Autohub.Pages.Documents;

/// <summary>
/// Create &amp; finish tab of an Autohub invoice: exactly what Bulk Create would do now, then Bulk Create and
/// Complete Review. Data comes from GET .../finish-summary; this model only supplies the header.
/// </summary>
public class FinishModel : PageModel
{
    private readonly IStagingPartsDocumentRepository _docs;

    public FinishModel(IStagingPartsDocumentRepository docs) => _docs = docs;

    public StagingPartsDocumentRow Doc { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        if (doc is null) return NotFound();
        Doc = doc;
        ViewData["Title"] = "Create & finish · " + (doc.InvoiceNumber ?? doc.OriginalFilename);
        return Page();
    }
}
