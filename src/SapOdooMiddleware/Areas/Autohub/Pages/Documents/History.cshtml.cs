using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Areas.Autohub.Pages.Documents;

/// <summary>History tab of an Autohub invoice (GET .../activity): who did what, when, to which line.</summary>
public class HistoryModel : PageModel
{
    private readonly IStagingPartsDocumentRepository _docs;

    public HistoryModel(IStagingPartsDocumentRepository docs) => _docs = docs;

    public StagingPartsDocumentRow Doc { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        if (doc is null) return NotFound();
        Doc = doc;
        ViewData["Title"] = "History · " + (doc.InvoiceNumber ?? doc.OriginalFilename);
        return Page();
    }
}
