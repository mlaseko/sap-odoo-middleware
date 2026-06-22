using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Areas.Autohub.Pages.Documents;

public class IndexModel : PageModel
{
    private readonly IStagingPartsDocumentRepository _docs;
    public IndexModel(IStagingPartsDocumentRepository docs) => _docs = docs;

    public IReadOnlyList<StagingPartsDocumentRow> Docs { get; private set; } = Array.Empty<StagingPartsDocumentRow>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Docs = await _docs.ListRecentAsync(50, ct);

        // Auto-refresh the list while anything is still being extracted.
        if (Docs.Any(d => d.Status is "extracting" or "uploaded"))
            ViewData["AutoRefresh"] = true;

        ViewData["Title"] = "Autohub Documents";
    }
}
