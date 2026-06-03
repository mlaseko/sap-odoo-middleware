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

    public async Task<PartsBulkCreateResult> BulkCreateAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        var currency = doc?.Currency;

        var toCreate = await _review.ListCreateNewAsync(documentId, ct);

        int created = 0, needsConfirmation = 0;
        var failures = new List<PartsBulkCreateFailure>();

        foreach (var line in toCreate)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerItemTimeout);

                var outcome = await _provisioning.ProvisionAsync(line, currency, timeoutCts.Token);
                switch (outcome.Status)
                {
                    case "created": created++; break;
                    case "needs_confirmation": needsConfirmation++; break;
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

        _logger.LogInformation(
            "Autohub bulk-create for {Id}: attempted {Attempted}, created {Created}, needsConfirmation {Needs}, failed {Failed}.",
            documentId, toCreate.Count, created, needsConfirmation, failures.Count);

        return new PartsBulkCreateResult(toCreate.Count, created, needsConfirmation, failures.Count, failures);
    }
}

public record PartsBulkCreateResult(int Attempted, int Created, int NeedsConfirmation, int Failed, List<PartsBulkCreateFailure> Failures);
public record PartsBulkCreateFailure(Guid LineId, string? ArticleNumber, string Error);
