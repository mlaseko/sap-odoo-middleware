using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>Per-line provisioning outcome. Status ∈ {created, failed, needs_confirmation}.</summary>
public sealed record PartsProvisioningOutcome(string Status, string? ItemCode, string? Error);

public interface IPartsItemProvisioningService
{
    Task<PartsProvisioningOutcome> ProvisionAsync(PartsProvisioningLine line, string? currency, CancellationToken ct);
}

/// <summary>
/// Turns one reviewed 'create_new' parts line into a real SAP item (D-series pipeline §10):
///   filter OEMs → enrich (DGX, idempotent) → forex → price → allocate SKU → write OITM → bridge to
///   the Neon mirror so auto-match finds it next time. SAP is the system of record; the line is
///   only marked 'created' once the OITM write succeeds (the Neon bridge is best-effort and can be
///   re-published), which prevents a retry from minting a duplicate SAP item under a fresh SKU.
/// Persists its own outcome (RecordCreated / RecordCreateFailed); the bulk caller just tallies.
/// </summary>
public sealed class PartsItemProvisioningService : IPartsItemProvisioningService
{
    private readonly IEnrichmentService _enrichment;
    private readonly IForexConversionService _forex;
    private readonly IPricingCalculationService _pricing;
    private readonly ISkuGenerationService _sku;
    private readonly IOemFilterService _filter;
    private readonly ISapB1Service _sap;
    private readonly INeonBridgeService _bridge;
    private readonly IPartsReviewRepository _review;
    private readonly ILogger<PartsItemProvisioningService> _logger;

    public PartsItemProvisioningService(
        IEnrichmentService enrichment, IForexConversionService forex, IPricingCalculationService pricing,
        ISkuGenerationService sku, IOemFilterService filter, ISapB1Service sap, INeonBridgeService bridge,
        IPartsReviewRepository review, ILogger<PartsItemProvisioningService> logger)
    {
        _enrichment = enrichment;
        _forex = forex;
        _pricing = pricing;
        _sku = sku;
        _filter = filter;
        _sap = sap;
        _bridge = bridge;
        _review = review;
        _logger = logger;
    }

    public async Task<PartsProvisioningOutcome> ProvisionAsync(PartsProvisioningLine line, string? currency, CancellationToken ct)
    {
        var article = line.SupplierArticleNumber?.Trim();
        if (string.IsNullOrWhiteSpace(article))
            return await Fail(line.Id, "Line has no supplier article number.", ct);
        if (line.UnitPriceForeign is not > 0m)
            return await Fail(line.Id, "Line has no positive unit price.", ct);
        if (string.IsNullOrWhiteSpace(currency))
            return await Fail(line.Id, "Document currency is unknown; cannot convert cost.", ct);

        var filtered = _filter.Filter(line.OemNumbers, article, line.Brand).CleanOems;

        // Enrichment (idempotent re-fetch — the operator already confirmed any borrowed data during review).
        EnrichmentResponse enr;
        try
        {
            enr = await _enrichment.EnrichLineAsync(
                new EnrichmentInput(article, filtered, line.Brand, line.Description, null), ct);
        }
        catch (Exception ex)
        {
            return await Fail(line.Id, $"Enrichment failed: {ex.Message}", ct);
        }

        if (enr.ItemData is null)
            return await Fail(line.Id, "Enrichment returned no item data.", ct);
        if (enr.ConfirmationRequired && !line.EnrichmentConfirmed)
            return new PartsProvisioningOutcome("needs_confirmation", null,
                "Borrowed enrichment requires operator confirmation before creation.");

        var data = enr.ItemData;
        if (data.SuggestedItmsGrpCod is not { } groupCode)
            return await Fail(line.Id, "Enrichment did not return a SAP item group (suggested_itms_grp_cod).", ct);
        var prefix = string.IsNullOrWhiteSpace(data.SuggestedSkuPrefix) ? "GEN" : data.SuggestedSkuPrefix!.Trim();

        // Forex → landed cost (TZS), and the rate we used (for audit).
        decimal costTzs;
        try
        {
            costTzs = await _forex.ConvertToTzsAsync(line.UnitPriceForeign.Value, currency!, DateTime.UtcNow, ct);
        }
        catch (Exception ex)
        {
            return await Fail(line.Id, $"Forex conversion failed: {ex.Message}", ct);
        }
        var rate = Math.Round(costTzs / line.UnitPriceForeign.Value, 6, MidpointRounding.AwayFromZero);

        var prices = await _pricing.CalculateAsync(costTzs, line.Brand ?? "", ct);

        // Allocate the final ItemCode. NOTE: the counter is atomic but burns a number even if the
        // SAP write below fails — gaps in SAP item codes are acceptable; we never reuse/duplicate.
        var itemCode = await _sku.GenerateAsync(prefix, ct);
        var itemName = BuildItemName(filtered, article!);

        var sapReq = new SapAutohubItemRequest(
            ItemCode: itemCode,
            ItemName: itemName,
            ItemsGroupCode: groupCode,
            CostPrice: prices.Cost,
            RetailPrice: prices.Retail,
            WholesalePrice: prices.Wholesale,
            ArticleNumber: article!,
            Description: data.PrimaryDescription ?? line.Description,
            FitForAuto: data.FitForAuto,
            ImageUrl: data.ImageUrl);

        try
        {
            await _sap.CreateAutohubItemAsync(sapReq);
        }
        catch (Exception ex)
        {
            return await Fail(line.Id, $"SAP item write failed: {ex.Message}", ct);
        }

        // Bridge: stamp the SAP ItemCode onto the pre-enriched parts_catalog row so auto-match finds
        // it. Best-effort: the item already exists in SAP, so a bridge failure must NOT mark the line
        // failed (that would mint a duplicate on retry) — log it; an admin reconcile can re-link by id.
        if (enr.NeonOitmId is { } neonOitmId)
        {
            try
            {
                await _bridge.LinkAsync(neonOitmId, itemCode, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SAP item {ItemCode} created but Neon bridge link (oitm id {OitmId}) failed; reconcile required.",
                    itemCode, neonOitmId);
            }
        }
        else
        {
            _logger.LogWarning(
                "SAP item {ItemCode} created but enrichment returned no neon_oitm_id; cannot link the Neon mirror.",
                itemCode);
        }

        await _review.RecordCreatedAsync(line.Id, itemCode, prices.Cost, prices.Retail, prices.Wholesale, rate, ct);
        return new PartsProvisioningOutcome("created", itemCode, null);
    }

    /// <summary>D5 ItemName: up to five filtered OEMs then the supplier article, joined by '/'.</summary>
    private static string BuildItemName(IReadOnlyList<string> oems, string article)
    {
        var parts = oems.Take(5).ToList();
        parts.Add(article);
        var name = string.Join("/", parts);
        return name.Length > 200 ? name[..200] : name;
    }

    private async Task<PartsProvisioningOutcome> Fail(Guid lineId, string error, CancellationToken ct)
    {
        await _review.RecordCreateFailedAsync(lineId, error, ct);
        return new PartsProvisioningOutcome("failed", null, error);
    }
}
