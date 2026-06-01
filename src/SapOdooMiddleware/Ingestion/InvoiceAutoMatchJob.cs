using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// After extraction, pre-populates per-line review state by checking each line's article number
/// against SAP B1. Pending lines with no article stay pending; promotional lines become 'skip';
/// lines whose article exists in SAP become 'matched'. Only touches 'pending' lines, so it is
/// safe to re-run. On SAP outage it stops querying, leaves lines pending, and stamps the doc so
/// the operator can retry from the UI.
/// </summary>
public class InvoiceAutoMatchJob
{
    private readonly IStagingDocumentRepository _docs;
    private readonly IStagingDocumentLineRepository _lines;
    private readonly ISapB1Service _sap;
    private readonly ILogger<InvoiceAutoMatchJob> _logger;

    public InvoiceAutoMatchJob(
        IStagingDocumentRepository docs,
        IStagingDocumentLineRepository lines,
        ISapB1Service sap,
        ILogger<InvoiceAutoMatchJob> logger)
    {
        _docs = docs;
        _lines = lines;
        _sap = sap;
        _logger = logger;
    }

    /// <summary>Runs the auto-match pass. Returns (newlyMatched, stillPending) for the API path.</summary>
    public async Task<(int NewlyMatched, int StillPending)> RunAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null)
        {
            _logger.LogWarning("Auto-match skipped: document {Id} not found.", documentId);
            return (0, 0);
        }
        if (doc.Status is not ("extracted" or "reviewed"))
        {
            _logger.LogInformation("Auto-match skipped: document {Id} is '{Status}', not extracted.", documentId, doc.Status);
            return (0, 0);
        }

        var lines = await _lines.ListByDocumentAsync(documentId, ct);
        var pending = lines.Where(l => l.ReviewStatus == "pending").ToList();

        int newlyMatched = 0;
        int stillPending = 0;
        bool sapAvailable = true;

        foreach (var line in pending)
        {
            if (line.IsPromotional)
            {
                await _lines.SetReviewStatusAsync(line.Id, "skip", null, ct);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line.ArticleNumber))
            {
                stillPending++;
                continue;
            }

            if (!sapAvailable)
            {
                stillPending++;
                continue;
            }

            try
            {
                var article = line.ArticleNumber.Trim();
                if (await _sap.ItemExistsAsync(article))
                {
                    await _lines.SetReviewStatusAsync(line.Id, "matched", article, ct);
                    newlyMatched++;
                }
                else
                {
                    stillPending++;
                }
            }
            catch (Exception ex)
            {
                // Treat as SAP unreachable — stop querying, leave the rest pending, allow retry.
                _logger.LogWarning(ex, "SAP lookup failed during auto-match for document {Id}; leaving remaining lines pending.", documentId);
                sapAvailable = false;
                stillPending++;
            }
        }

        // Record the pass: AutoMatchedCount = total lines now in a matched/skip state.
        var counts = await _lines.GetStatusCountsAsync(documentId, ct);
        var matchedOrSkipped = counts.GetValueOrDefault("matched") + counts.GetValueOrDefault("skip");
        await _docs.SetAutoMatchedAsync(documentId, matchedOrSkipped, ct);

        _logger.LogInformation(
            "Auto-match for document {Id}: {Matched} newly matched, {Pending} still pending (sapAvailable={Sap}).",
            documentId, newlyMatched, stillPending, sapAvailable);

        return (newlyMatched, stillPending);
    }
}
