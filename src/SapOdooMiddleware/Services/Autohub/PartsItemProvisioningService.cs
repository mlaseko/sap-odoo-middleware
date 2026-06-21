using System.Text.Json;
using System.Text.Json.Serialization;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>Per-line provisioning outcome. Status ∈ {created, failed, needs_confirmation}.</summary>
public sealed record PartsProvisioningOutcome(string Status, string? ItemCode, string? Error);

/// <summary>
/// Operator-supplied values for manual creation, used when DGX enrichment could not classify the part
/// (no <c>suggested_itms_grp_cod</c> / <c>suggested_sku_prefix</c>). Lets the operator pick the SAP item
/// group and brand prefix directly so the line can still be created.
/// </summary>
public sealed record ManualItemOverride(int ItemsGroupCode, string SkuPrefix, string? Description, string? FitForAuto, string? ImageUrl);

public interface IPartsItemProvisioningService
{
    Task<PartsProvisioningOutcome> ProvisionAsync(PartsProvisioningLine line, string? currency, CancellationToken ct);

    /// <summary>
    /// Create a SAP item from operator-supplied item group + SKU prefix, bypassing the enrichment
    /// <c>item_data</c>/group requirement (for parts DGX can't classify). Mirrors a fresh own-identity
    /// Neon row so future invoices auto-match it. Persists its own created/failed outcome.
    /// </summary>
    Task<PartsProvisioningOutcome> ProvisionManualAsync(PartsProvisioningLine line, string? currency, ManualItemOverride manual, CancellationToken ct);
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
    // Autohub items are created in the Autohub company (Companies:Autohub:SapB1), NOT the default Lubes
    // company — so this is the Autohub-bound SAP connection, not the shared ISapB1Service.
    private readonly IAutohubSapB1Service _sap;
    private readonly INeonBridgeService _bridge;
    private readonly IPartsReviewRepository _review;
    private readonly ILogger<PartsItemProvisioningService> _logger;

    public PartsItemProvisioningService(
        IEnrichmentService enrichment, IForexConversionService forex, IPricingCalculationService pricing,
        ISkuGenerationService sku, IOemFilterService filter, IAutohubSapB1Service sap, INeonBridgeService bridge,
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

    private static readonly JsonSerializerOptions EnrichmentJson = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

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

        // Prefer the enrichment persisted at review time (what the operator saw/confirmed); only
        // re-call DGX if the line was never enriched (e.g. created straight from a quick match).
        // The re-fetch is idempotent — DGX returns the same oitm row for the same article+brand.
        EnrichmentResponse enr;
        if (!string.IsNullOrWhiteSpace(line.EnrichmentPayloadJson))
        {
            try
            {
                enr = JsonSerializer.Deserialize<EnrichmentResponse>(line.EnrichmentPayloadJson, EnrichmentJson)
                      ?? throw new InvalidOperationException("empty payload");
            }
            catch (Exception ex)
            {
                return await Fail(line.Id, $"Stored enrichment could not be read: {ex.Message}", ct);
            }
        }
        else
        {
            try
            {
                enr = await _enrichment.EnrichLineAsync(
                    new EnrichmentInput(article, filtered, line.Brand, line.Description, null), ct);
            }
            catch (Exception ex)
            {
                return await Fail(line.Id, $"Enrichment failed: {ex.Message}", ct);
            }
        }

        if (enr.ItemData is null)
            return await Fail(line.Id, "Enrichment returned no item data.", ct);
        // Only a GENUINE cross-supplier borrow (another supplier's data applied to our item) needs operator
        // sign-off before creating — same-supplier / own-data enrichment creates straight through. DGX's
        // blanket confirmation_required fires on nearly everything, so it is NOT the gate (it blocked bulk
        // creation for every borrowed/unmatched line). The MatchStrategy already records the cross-supplier
        // decision the router made.
        if (EnrichmentStrategies.IsCrossSupplierStrategy(line.MatchStrategy) && !line.EnrichmentConfirmed)
            return new PartsProvisioningOutcome("needs_confirmation", null,
                "Cross-supplier borrowed enrichment requires operator confirmation before creation.");

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
        if (prices.Cost <= 0m)
            return await Fail(line.Id, "Computed price-list 01 (cost) is zero — check the forex rate and pricing config before creating the SAP item.", ct);

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
                if (EnrichmentStrategies.IsCrossSupplierStrategy(line.MatchStrategy))
                {
                    // Cross-supplier: never write our code to the donor (different supplier). Mint an
                    // own-identity oitm row for the new SAP item and repoint the line at it, so future
                    // invoices for this (brand, article) auto-match instead of minting another duplicate.
                    var newId = await _bridge.CreateOwnIdentityRowAsync(
                        neonOitmId, itemCode, EnrichmentStrategies.ResolveOwnIdentitySource(line.MatchStrategy),
                        line.SupplierArticleNumber, line.Brand, ct);
                    if (newId is { } nid)
                        await _review.UpdateNeonOitmIdAsync(line.Id, nid, ct);
                    else
                        _logger.LogWarning(
                            "SAP item {ItemCode} created but donor oitm {OitmId} missing; own-identity row not minted.",
                            itemCode, neonOitmId);
                }
                else
                {
                    var link = await _bridge.LinkAsync(neonOitmId, itemCode, ct);
                    if (link.Status == NeonBridgeLinkStatus.BlockedByExisting)
                        _logger.LogError(
                            "SAP item {ItemCode} created but oitm id {OitmId} was already linked to '{Existing}' — likely a duplicate SAP item; reconcile.",
                            itemCode, neonOitmId, link.ExistingItemCode);
                    else if (link.Status == NeonBridgeLinkStatus.NotFound)
                        _logger.LogWarning(
                            "SAP item {ItemCode} created but oitm id {OitmId} not found; cannot link the Neon mirror.",
                            itemCode, neonOitmId);
                }
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

    /// <summary>
    /// Manual create: the operator supplies the SAP item group + prefix that enrichment couldn't.
    /// Self-contained (parallel to <see cref="ProvisionAsync"/>, NOT a refactor of it, so the proven
    /// enrichment path stays byte-identical): validate → forex → price → allocate SKU → write OITM →
    /// mint a fresh Neon mirror row (no donor) so future invoices auto-match. No DGX call, no
    /// enrichment-confirmation gate. Persists its own created/failed outcome.
    /// </summary>
    public async Task<PartsProvisioningOutcome> ProvisionManualAsync(
        PartsProvisioningLine line, string? currency, ManualItemOverride manual, CancellationToken ct)
    {
        var article = line.SupplierArticleNumber?.Trim();
        if (string.IsNullOrWhiteSpace(article))
            return await Fail(line.Id, "Line has no supplier article number.", ct);
        if (line.UnitPriceForeign is not > 0m)
            return await Fail(line.Id, "Line has no positive unit price.", ct);
        if (string.IsNullOrWhiteSpace(currency))
            return await Fail(line.Id, "Document currency is unknown; cannot convert cost.", ct);
        if (manual.ItemsGroupCode <= 0)
            return await Fail(line.Id, "A SAP item group is required for manual creation.", ct);

        var prefix = string.IsNullOrWhiteSpace(manual.SkuPrefix) ? "GEN" : manual.SkuPrefix.Trim();
        var filtered = _filter.Filter(line.OemNumbers, article, line.Brand).CleanOems;

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
        if (prices.Cost <= 0m)
            return await Fail(line.Id, "Computed price-list 01 (cost) is zero — check the forex rate and pricing config before creating the SAP item.", ct);

        // Same atomic-counter caveat as ProvisionAsync: the number is burned even if the SAP write fails.
        var itemCode = await _sku.GenerateAsync(prefix, ct);
        var itemName = BuildItemName(filtered, article!);

        var sapReq = new SapAutohubItemRequest(
            ItemCode: itemCode,
            ItemName: itemName,
            ItemsGroupCode: manual.ItemsGroupCode,
            CostPrice: prices.Cost,
            RetailPrice: prices.Retail,
            WholesalePrice: prices.Wholesale,
            ArticleNumber: article!,
            Description: manual.Description ?? line.Description,
            FitForAuto: manual.FitForAuto,
            ImageUrl: manual.ImageUrl);

        try
        {
            await _sap.CreateAutohubItemAsync(sapReq);
        }
        catch (Exception ex)
        {
            return await Fail(line.Id, $"SAP item write failed: {ex.Message}", ct);
        }

        // Mirror a fresh own-identity Neon row (no donor to copy from) so future invoices for this
        // (supplier, article) auto-match instead of repeatedly landing in needs_manual. Best-effort:
        // the SAP item already exists, so a mirror failure must NOT fail the line (a retry would mint a
        // duplicate). Log for reconcile.
        try
        {
            var supplier = string.IsNullOrWhiteSpace(line.Brand) ? null : line.Brand;
            await _bridge.CreateFreshRowAsync(itemCode, article!, supplier, filtered, "manual_create", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Manual create: SAP item {ItemCode} created but the Neon mirror insert failed; reconcile required.", itemCode);
        }

        await _review.RecordCreatedAsync(line.Id, itemCode, prices.Cost, prices.Retail, prices.Wholesale, rate, ct);
        _logger.LogInformation("Manual create: line {LineId} → SAP item {ItemCode} (group {Group}, prefix {Prefix}).",
            line.Id, itemCode, manual.ItemsGroupCode, prefix);
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
