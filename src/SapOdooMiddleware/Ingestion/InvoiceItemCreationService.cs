using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
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
    private readonly TimeSpan _perItemTimeout;

    private readonly ILubesItemProvisioningService _provisioning;
    private readonly IStagingDocumentLineRepository _lines;
    private readonly ILogger<InvoiceItemCreationService> _logger;

    public InvoiceItemCreationService(
        ILubesItemProvisioningService provisioning,
        IStagingDocumentLineRepository lines,
        IOptions<BulkCreateSettings> bulkCreate,
        ILogger<InvoiceItemCreationService> logger)
    {
        _provisioning = provisioning;
        _lines = lines;
        // Per-line cap covers scrape + DGX classifier (/classify + /classify_family, up to 180s) + SAP.
        // Configurable so a cold/slow DGX doesn't force premature 'create_failed' on every line.
        _perItemTimeout = TimeSpan.FromSeconds(Math.Max(1, bulkCreate.Value.PerItemTimeoutSeconds));
        _logger = logger;
    }

    public async Task<BulkCreateResult> BulkCreateAsync(Guid documentId, CancellationToken ct)
    {
        var all = await _lines.ListByDocumentAsync(documentId, ct);
        // Include 'create_failed' so re-running Bulk Create retries lines that failed earlier (e.g. SAP
        // was offline). Safe: provisioning checks SAP for an existing item first (the "recovered" path),
        // so retrying never double-creates.
        var toCreate = all.Where(l => l.ReviewStatus is "create_new" or "create_failed").ToList();

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
                timeoutCts.CancelAfter(_perItemTimeout);

                // Pass the invoice line description so provisioning can route Meguin products
                // (LM subsidiary, names start with "Meguin") to the meguin.com scraper.
                var req = new LubesProvisioningRequest(article, line.UnitPrice.Value, SupplierName: line.Description);
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
                await Fail(line, $"Timed out after {_perItemTimeout.TotalSeconds:N0}s.", failures, ct);
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

    /// <summary>
    /// Provisions a single line with a reviewer-assigned Odoo category override — the resolution path for a
    /// line that failed on low category confidence. Records the per-line outcome. Returns null on success,
    /// or a <see cref="BulkCreateFailure"/> describing why it failed.
    /// </summary>
    public async Task<BulkCreateFailure?> CreateLineWithCategoryAsync(
        Guid documentId, Guid lineId, string odooCategoryExternalId, string odooCategoryName, CancellationToken ct)
    {
        var line = await _lines.GetByIdAsync(lineId, ct);
        if (line is null || line.DocumentId != documentId)
            return new BulkCreateFailure(lineId, null, "Line not found.");

        var article = line.ArticleNumber?.Trim();
        if (string.IsNullOrWhiteSpace(article))
        {
            await _lines.RecordCreateFailedAsync(lineId, "Line has no article number.", ct);
            return new BulkCreateFailure(lineId, null, "Line has no article number.");
        }
        if (line.UnitPrice is not > 0m)
        {
            await _lines.RecordCreateFailedAsync(lineId, "Line has no positive EUR unit cost.", ct);
            return new BulkCreateFailure(lineId, article, "Line has no positive EUR unit cost.");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_perItemTimeout);

            var req = new LubesProvisioningRequest(article, line.UnitPrice.Value,
                SupplierName: line.Description,
                OdooCategoryOverrideExternalId: odooCategoryExternalId,
                OdooCategoryOverrideName: odooCategoryName);
            var result = await _provisioning.ProvisionAsync(req, timeoutCts.Token);

            if (result.Status is "created" or "recovered")
            {
                await _lines.RecordCreatedAsync(lineId, result.ItemCode, ct);
                return null;
            }

            var reason = result.ErrorMessage ?? result.ReviewReason ?? $"Provisioning returned '{result.Status}'.";
            await _lines.RecordCreateFailedAsync(lineId, reason, ct);
            return new BulkCreateFailure(lineId, article, reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host shutdown
        }
        catch (OperationCanceledException)
        {
            var msg = $"Timed out after {_perItemTimeout.TotalSeconds:N0}s.";
            await _lines.RecordCreateFailedAsync(lineId, msg, ct);
            return new BulkCreateFailure(lineId, article, msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Create-with-category failed for line {LineId} (article {Article}).", lineId, article);
            await _lines.RecordCreateFailedAsync(lineId, ex.Message, ct);
            return new BulkCreateFailure(lineId, article, ex.Message);
        }
    }
}

public record BulkCreateResult(int Attempted, int Created, int Failed, List<BulkCreateFailure> Failures);
public record BulkCreateFailure(Guid LineId, string? ArticleNumber, string Error);
