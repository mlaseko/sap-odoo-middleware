using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Pages.Documents;

public class IndexModel : PageModel
{
    private readonly IStagingDocumentRepository _docs;
    public IndexModel(IStagingDocumentRepository docs) => _docs = docs;

    public IReadOnlyList<StagingDocumentRow> Docs { get; private set; } = Array.Empty<StagingDocumentRow>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Docs = await _docs.ListRecentAsync(50, ct);

        // Auto-refresh the list while anything is still being extracted.
        if (Docs.Any(d => d.Status == "extracting" || d.Status == "uploaded"))
            ViewData["AutoRefresh"] = true;

        ViewData["Title"] = "Documents";
    }
}
