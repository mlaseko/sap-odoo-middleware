using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Areas.Autohub.Pages.Documents;

public class IndexModel : PageModel
{
    private readonly IStagingPartsDocumentRepository _docs;
    private readonly SapOdooMiddleware.Services.Autohub.IInvoiceIssuesService _issues;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(IStagingPartsDocumentRepository docs, SapOdooMiddleware.Services.Autohub.IInvoiceIssuesService issues,
        ILogger<IndexModel> logger)
    {
        _docs = docs;
        _issues = issues;
        _logger = logger;
    }

    public IReadOnlyList<StagingPartsDocumentRow> Docs { get; private set; } = Array.Empty<StagingPartsDocumentRow>();

    /// <summary>Line progress per invoice (absent while extracting, or if it could not be worked out).</summary>
    public IReadOnlyDictionary<Guid, SapOdooMiddleware.Services.Autohub.InvoiceProgress> Progress { get; private set; }
        = new Dictionary<Guid, SapOdooMiddleware.Services.Autohub.InvoiceProgress>();

    /// <summary>?show=attention — only invoices still being reviewed that have work left.</summary>
    public bool OnlyAttention { get; private set; }

    public int AttentionCount { get; private set; }

    public bool NeedsWork(StagingPartsDocumentRow d) =>
        d.Status == "extracted" && Progress.TryGetValue(d.Id, out var p) && (p.Attention > 0 || p.Ready > 0);

    /// <summary>Where "Continue" goes: the first step with work left.</summary>
    public string ContinueUrl(StagingPartsDocumentRow d)
    {
        var url = "/autohub/documents/" + d.Id;
        if (d.Status != "extracted" || !Progress.TryGetValue(d.Id, out var p)) return url;
        return p.Attention > 0 ? url + "/issues" : url + "/finish";
    }

    public async Task OnGetAsync(string? show, CancellationToken ct)
    {
        Docs = await _docs.ListRecentAsync(50, ct);
        try
        {
            Progress = await _issues.GetProgressAsync(
                Docs.Where(d => d.Status is "extracted" or "reviewed").Select(d => d.Id).ToList(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not work out invoice progress for the list.");
        }
        AttentionCount = Docs.Count(NeedsWork);
        OnlyAttention = show == "attention";
        if (OnlyAttention) Docs = Docs.Where(NeedsWork).ToList();

        // Auto-refresh the list while anything is still being extracted.
        if (Docs.Any(d => d.Status is "extracting" or "uploaded"))
            ViewData["AutoRefresh"] = true;

        ViewData["Title"] = "Autohub Documents";
    }
}
