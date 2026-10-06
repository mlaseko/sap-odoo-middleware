using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Bulk-creates SAP items for every Autohub line marked 'create_new'. Sequential (the DI API does
/// not tolerate concurrent writes), continues past individual failures, and applies a per-item
/// timeout so one slow enrich/create can't hang the batch. Per-line outcomes are persisted by the
/// provisioning service. Mirrors the Lubes InvoiceItemCreationService.
/// </summary>
public sealed class PartsItemCreationService
{
    private static readonly TimeSpan PerItemTimeout = TimeSpan.FromSeconds(120);

    private readonly IPartsItemProvisioningService _provisioning;
    private readonly IPartsReviewRepository _review;
    private readonly IStagingPartsDocumentRepository _docs;
    private readonly ILogger<PartsItemCreationService> _logger;

    public PartsItemCreationService(
        IPartsItemProvisioningService provisioning,
        IPartsReviewRepository review,
        IStagingPartsDocumentRepository docs,
        ILogger<PartsItemCreationService> logger)
    {
        _provisioning = provisioning;
        _review = review;
        _docs = docs;
        _logger = logger;
    }

    public async Task<PartsBulkCreateResult> BulkCreateAsync(
        Guid documentId, CancellationToken ct,
        IReadOnlyList<BulkCreateLineSelection>? selections = null)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        var currency = doc?.Currency;

        var toCreate = await _review.ListCreateNewAsync(documentId, ct);

        int created = 0, needsConfirmation = 0, held = 0;
        var failures = new List<PartsBulkCreateFailure>();

        // The price review gate posts a specific list of reviewed lines (with optional
        // per-line price overrides); legacy callers send no list and get every eligible
        // line, exactly as before. A requested id that is not eligible fails that line
        // only — the reviewed batch is never silently widened or narrowed.
        List<(PartsProvisioningLine Line, PartsLineOverride? Override)> work;
        if (selections is null)
        {
            work = toCreate.Select(l => (l, (PartsLineOverride?)null)).ToList();
        }
        else
        {
            var eligible = toCreate.ToDictionary(l => l.Id);
            work = new List<(PartsProvisioningLine, PartsLineOverride?)>(selections.Count);
            foreach (var s in selections)
            {
                if (eligible.TryGetValue(s.LineId, out var line))
                    work.Add((line, s.PriceOverride));
                else
                    failures.Add(new PartsBulkCreateFailure(s.LineId, null,
                        "Line not found on this document or not eligible (ReviewStatus must be create_new/create_failed)."));
            }
        }

        foreach (var (line, priceOverride) in work)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerItemTimeout);

                var outcome = await _provisioning.ProvisionAsync(line, currency, timeoutCts.Token, priceOverride);
                switch (outcome.Status)
                {
                    case "created": created++; break;
                    case "needs_confirmation": needsConfirmation++; break;
                    // Holds are waiting on an operator decision, NOT failures — the provisioning service has
                    // already parked the line with its own status/reason; just tally them apart.
                    case "needs_manufacturer":
                    case "prefix_exhausted": held++; break;
                    default: failures.Add(new PartsBulkCreateFailure(line.Id, line.SupplierArticleNumber, outcome.Error ?? "failed")); break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // host shutdown — abort the batch
            }
            catch (OperationCanceledException)
            {
                await _review.RecordCreateFailedAsync(line.Id, $"Timed out after {PerItemTimeout.TotalSeconds:N0}s.", ct);
                failures.Add(new PartsBulkCreateFailure(line.Id, line.SupplierArticleNumber, "timed out"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk-create failed for line {LineId} (article {Article}).", line.Id, line.SupplierArticleNumber);
                await _review.RecordCreateFailedAsync(line.Id, ex.Message, ct);
                failures.Add(new PartsBulkCreateFailure(line.Id, line.SupplierArticleNumber, ex.Message));
            }
        }

        var attempted = selections?.Count ?? toCreate.Count;
        _logger.LogInformation(
            "Autohub bulk-create for {Id}: attempted {Attempted}, created {Created}, needsConfirmation {Needs}, held {Held}, failed {Failed}.",
            documentId, attempted, created, needsConfirmation, held, failures.Count);

        return new PartsBulkCreateResult(attempted, created, needsConfirmation, held, failures.Count, failures);
    }
}

/// <summary>One reviewed line the gate asks Bulk Create to post, with its price decision.</summary>
public sealed record BulkCreateLineSelection(Guid LineId, PartsLineOverride? PriceOverride);

public record PartsBulkCreateResult(int Attempted, int Created, int NeedsConfirmation, int Held, int Failed, List<PartsBulkCreateFailure> Failures);
public record PartsBulkCreateFailure(Guid LineId, string? ArticleNumber, string Error);
