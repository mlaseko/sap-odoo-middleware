using System.Text.Json;
using System.Text.Json.Serialization;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>Per-code reconcile result: Result ∈ {created, skipped, failed}.</summary>
public sealed record SapReconcileOutcome(string ItemCode, string Result, string? Detail);

/// <summary>
/// Recovery tool for Neon-mirror rows whose SAP item is missing — item codes that exist in
/// <c>oitm.item_code</c> but not in the Autohub SAP company (an old-build write bug left the mirror
/// ahead of SAP). Re-creates each SAP item under its ORIGINAL code from the create-time data still
/// stored on the source staging line (prices) and its enrichment payload (item group, OEMs, name), so
/// Neon and SAP reconcile without minting new codes. Idempotent: a code already present in SAP is
/// skipped, so re-running is safe.
/// </summary>
public sealed class PartsSapReconciliationService
{
    private readonly IPartsReviewRepository _review;
    private readonly IAutohubSapB1Service _sap;
    private readonly ILogger<PartsSapReconciliationService> _logger;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public PartsSapReconciliationService(
        IPartsReviewRepository review, IAutohubSapB1Service sap, ILogger<PartsSapReconciliationService> logger)
    {
        _review = review;
        _sap = sap;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SapReconcileOutcome>> ReconcileAsync(IReadOnlyList<string> itemCodes, CancellationToken ct)
    {
        var results = new List<SapReconcileOutcome>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in itemCodes)
        {
            ct.ThrowIfCancellationRequested();
            var code = raw?.Trim();
            if (string.IsNullOrWhiteSpace(code) || !seen.Add(code)) continue;

            try
            {
                // Idempotent: never re-create something SAP already has.
                if (await _sap.ItemExistsAsync(code))
                {
                    results.Add(new SapReconcileOutcome(code, "skipped", "already exists in SAP"));
                    continue;
                }

                var data = await _review.GetCreatedLineForReconcileAsync(code, ct);
                if (data is null)
                {
                    results.Add(new SapReconcileOutcome(code, "skipped", "no 'created' staging line found for this code"));
                    continue;
                }
                if (data.Pl01Tzs is not > 0m || data.Pl03Tzs is null || data.Pl05Tzs is null)
                {
                    results.Add(new SapReconcileOutcome(code, "failed", "stored prices missing on the source line — cannot re-create"));
                    continue;
                }

                EnrichmentItemData? item = null;
                if (!string.IsNullOrWhiteSpace(data.EnrichmentPayloadJson))
                {
                    try { item = JsonSerializer.Deserialize<EnrichmentResponse>(data.EnrichmentPayloadJson, Json)?.ItemData; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Reconcile {Code}: could not parse stored enrichment payload.", code); }
                }
                if (item?.SuggestedItmsGrpCod is not int group || group <= 0)
                {
                    results.Add(new SapReconcileOutcome(code, "failed", "no SAP item group in the stored enrichment — cannot re-create"));
                    continue;
                }

                var article = data.Article ?? string.Empty;
                var itemName = PartsItemProvisioningService.BuildItemName(item.FilteredOems ?? new List<string>(), article);
                var req = new SapAutohubItemRequest(
                    ItemCode:       code,
                    ItemName:       itemName,
                    ItemsGroupCode: group,
                    CostPrice:      data.Pl01Tzs.Value,
                    RetailPrice:    data.Pl03Tzs.Value,
                    WholesalePrice: data.Pl05Tzs.Value,
                    ArticleNumber:  article,
                    PartName:       item.PrimaryDescription ?? data.Description,
                    Manufacturer:   data.Brand);

                await _sap.CreateAutohubItemAsync(req);
                results.Add(new SapReconcileOutcome(code, "created", $"group {group}"));
                _logger.LogInformation("Reconcile: re-created SAP item {Code} (group {Group}) from its stored create-time data.", code, group);
            }
            catch (Exception ex)
            {
                results.Add(new SapReconcileOutcome(code, "failed", ex.Message));
                _logger.LogError(ex, "Reconcile: failed to re-create SAP item {Code}.", code);
            }
        }

        return results;
    }
}
