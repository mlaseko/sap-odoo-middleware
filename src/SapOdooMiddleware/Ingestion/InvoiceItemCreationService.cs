using SapOdooMiddleware.ItemProvisioning;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// Bulk-creates items for every line marked 'create_new' by reusing the Phase 1 provisioning
/// service (the same logic behind POST /api/items). Runs sequentially because the SAP DI API
/// does not tolerate concurrent writes; continues past individual failures; applies a per-item
/// timeout so one slow create cannot hang the batch. Per-line outcomes are persisted.
/// </summary>
public class InvoiceItemCreationService
{
    private static readonly TimeSpan PerItemTimeout = TimeSpan.FromSeconds(30);

    private readonly ILubesItemProvisioningService _provisioning;
    private readonly IStagingDocumentLineRepository _lines;
    private readonly ILogger<InvoiceItemCreationService> _logger;

    public InvoiceItemCreationService(
        ILubesItemProvisioningService provisioning,
        IStagingDocumentLineRepository lines,
        ILogger<InvoiceItemCreationService> logger)
    {
        _provisioning = provisioning;
        _lines = lines;
        _logger = logger;
    }

    public async Task<BulkCreateResult> BulkCreateAsync(Guid documentId, CancellationToken ct)
    {
        var all = await _lines.ListByDocumentAsync(documentId, ct);
        var toCreate = all.Where(l => l.ReviewStatus == "create_new").ToList();

        int created = 0;
        var failures = new List<BulkCreateFailure>();

        foreach (var line in toCreate)
        {
            ct.ThrowIfCancellationRequested();

            var article = line.ArticleNumber?.Trim();
            if (string.IsNullOrWhiteSpace(article))
            {
                await Fail(line, "Line has no article number.", failures, ct);
                continue;
            }
            if (line.UnitPrice is not > 0m)
            {
                await Fail(line, "Line has no positive EUR unit cost.", failures, ct);
                continue;
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerItemTimeout);

                var req = new LubesProvisioningRequest(article, line.UnitPrice.Value);
                var result = await _provisioning.ProvisionAsync(req, timeoutCts.Token);

                if (result.Status is "created" or "recovered")
                {
                    await _lines.RecordCreatedAsync(line.Id, result.ItemCode, ct);
                    created++;
                }
                else
                {
                    var reason = result.ErrorMessage ?? result.ReviewReason ?? $"Provisioning returned '{result.Status}'.";
                    await Fail(line, reason, failures, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // host shutdown — abort the batch
            }
            catch (OperationCanceledException)
            {
                await Fail(line, $"Timed out after {PerItemTimeout.TotalSeconds:N0}s.", failures, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk-create failed for line {LineId} (article {Article}).", line.Id, article);
                await Fail(line, ex.Message, failures, ct);
            }
        }

        _logger.LogInformation(
            "Bulk-create for document {Id}: attempted {Attempted}, created {Created}, failed {Failed}.",
            documentId, toCreate.Count, created, failures.Count);

        return new BulkCreateResult(toCreate.Count, created, failures.Count, failures);
    }

    private async Task Fail(StagingDocumentLineRow line, string error, List<BulkCreateFailure> failures, CancellationToken ct)
    {
        await _lines.RecordCreateFailedAsync(line.Id, error, ct);
        failures.Add(new BulkCreateFailure(line.Id, line.ArticleNumber, error));
    }
}

public record BulkCreateResult(int Attempted, int Created, int Failed, List<BulkCreateFailure> Failures);
public record BulkCreateFailure(Guid LineId, string? ArticleNumber, string Error);
