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

    /// <summary>
    /// Manual create for the given lines using an operator-supplied SAP item group + SKU prefix (for parts
    /// DGX couldn't classify). Same sequential/timeout/continue-on-failure shape as <see cref="BulkCreateAsync"/>.
    /// Lines are taken as-is regardless of ReviewStatus (the operator chose them); enrichment is bypassed.
    /// </summary>
    public async Task<PartsBulkCreateResult> BulkCreateManualAsync(
        Guid documentId, IReadOnlyList<Guid> lineIds, ManualItemOverride manual, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        var currency = doc?.Currency;

        int created = 0;
        var failures = new List<PartsBulkCreateFailure>();

        foreach (var lineId in lineIds)
        {
            ct.ThrowIfCancellationRequested();

            var row = await _review.GetByIdAsync(lineId, ct);
            if (row is null || row.DocumentId != documentId)
            {
                failures.Add(new PartsBulkCreateFailure(lineId, null, "line not found in this document"));
                continue;
            }

            // Manual create ignores enrichment, so the donor/payload fields are null by design.
            var provLine = new PartsProvisioningLine(
                Id: row.Id, SupplierArticleNumber: row.SupplierArticleNumber, OemNumbers: row.OemNumbers,
                Brand: row.Brand, Description: row.Description, UnitPriceForeign: row.UnitPriceForeign,
                EnrichmentConfirmed: false, NeonOitmId: null, EnrichmentPayloadJson: null, MatchStrategy: row.MatchStrategy);

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerItemTimeout);

                var outcome = await _provisioning.ProvisionManualAsync(provLine, currency, manual, timeoutCts.Token);
                if (outcome.Status == "created") created++;
                else failures.Add(new PartsBulkCreateFailure(lineId, row.SupplierArticleNumber, outcome.Error ?? "failed"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // host shutdown — abort the batch
            }
            catch (OperationCanceledException)
            {
                await _review.RecordCreateFailedAsync(lineId, $"Timed out after {PerItemTimeout.TotalSeconds:N0}s.", ct);
                failures.Add(new PartsBulkCreateFailure(lineId, row.SupplierArticleNumber, "timed out"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual bulk-create failed for line {LineId} (article {Article}).", lineId, row.SupplierArticleNumber);
                await _review.RecordCreateFailedAsync(lineId, ex.Message, ct);
                failures.Add(new PartsBulkCreateFailure(lineId, row.SupplierArticleNumber, ex.Message));
            }
        }

        _logger.LogInformation(
            "Autohub manual bulk-create for {Id}: attempted {Attempted}, created {Created}, failed {Failed} (group {Group}, prefix {Prefix}).",
            documentId, lineIds.Count, created, failures.Count, manual.ItemsGroupCode, manual.SkuPrefix);

        return new PartsBulkCreateResult(lineIds.Count, created, 0, failures.Count, failures);
    }
}

public record PartsBulkCreateResult(int Attempted, int Created, int NeedsConfirmation, int Failed, List<PartsBulkCreateFailure> Failures);
public record PartsBulkCreateFailure(Guid LineId, string? ArticleNumber, string Error);
