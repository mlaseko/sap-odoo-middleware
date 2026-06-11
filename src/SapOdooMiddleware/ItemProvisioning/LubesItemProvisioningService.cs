using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MolasLubes.Infrastructure.Integrations.LiquiMoly;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Integrations.Classifier;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Pricing;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.ItemProvisioning;

public interface ILubesItemProvisioningService
{
    Task<LubesProvisioningResult> ProvisionAsync(LubesProvisioningRequest request, CancellationToken ct);
}

/// <summary>
/// Orchestrates end-to-end provisioning of one Liqui Moly item.
///
/// The full data pipeline (scrape → classify category + family → price) runs and is
/// validated BEFORE any side-effecting write. Only then does it touch SAP:
///   - if the item does not exist, it is created;
///   - if it already exists, only blank SAP fields are filled (idempotent recovery).
/// The Neon upsert always runs (it is idempotent via ON CONFLICT), so re-POSTing the
/// same article number is safe and heals any prior half-created state.
/// SAP is the system of record: nothing is written if pre-flight validation fails.
/// </summary>
public class LubesItemProvisioningService : ILubesItemProvisioningService
{
    private readonly ICategoryClassifier            _classifier;
    private readonly IPricingCalculator             _pricing;
    private readonly ISapB1Service                  _sap;
    private readonly INeonLiquiMolyRepository       _lmRepo;
    private readonly INeonProductRepository         _productRepo;
    private readonly LiquiMolyProductScraperService _scraper;
    private readonly PricingSettings                _pricingSettings;
    private readonly ILogger<LubesItemProvisioningService> _logger;

    public LubesItemProvisioningService(
        ICategoryClassifier classifier,
        IPricingCalculator pricing,
        ISapB1Service sap,
        INeonLiquiMolyRepository lmRepo,
        INeonProductRepository productRepo,
        LiquiMolyProductScraperService scraper,
        IOptions<PricingSettings> pricingSettings,
        ILogger<LubesItemProvisioningService> logger)
    {
        _classifier      = classifier;
        _pricing         = pricing;
        _sap             = sap;
        _lmRepo          = lmRepo;
        _productRepo     = productRepo;
        _scraper         = scraper;
        _pricingSettings = pricingSettings.Value;
        _logger          = logger;
    }

    // Layer 2: accept a DGX SAP-family classification flagged needs_review when its confidence clears
    // this bar (DGX is non-deterministic and flags borderline-but-usable calls). Below it, the item is
    // sent to manual review instead of guessing a group.
    private const double MinFamilyConfidence = 0.70;

    // Layer 1: deterministic SAP-family overrides for product patterns we know with certainty and where
    // DGX has shown blind spots (e.g. it confidently calls coolant "Additives"). Matched against the LM
    // product name BEFORE calling DGX; first match wins, DGX is skipped.
    // NOTE: the group codes below must match the live SAP OITB item-group codes — verify before deploy.
    private static readonly (Regex Pattern, int Code, string Name, string Rule)[] FamilyOverrides =
    {
        (new Regex(@"\b(coolant|antifreeze|kfs)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            104, "Repair aids/service products", "name matches coolant/antifreeze"),
        (new Regex(@"^\s*pro-?line\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            112, "Workshop Pro-Line", "name starts with Pro-Line"),
        (new Regex(@"\b(motorbike|motorcycle|4t bike)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            110, "Motor Bike", "name matches motorbike/motorcycle"),
    };

    private static (int Code, string Name, string Rule)? MatchFamilyOverride(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName)) return null;
        foreach (var o in FamilyOverrides)
            if (o.Pattern.IsMatch(productName))
                return (o.Code, o.Name, o.Rule);
        return null;
    }

    public async Task<LubesProvisioningResult> ProvisionAsync(LubesProvisioningRequest req, CancellationToken ct)
    {
        var code = req.ArticleNumber?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(code))
            return new LubesProvisioningResult("failed", "", ErrorMessage: "ArticleNumber is required.");
        if (req.EurCost <= 0m)
            return new LubesProvisioningResult("failed", code, ErrorMessage: "EurCost must be > 0.");

        // ============================================================
        // DATA PIPELINE — gather + validate everything before any SAP/Neon item write.
        // (The Liqui Moly cache upsert is idempotent and is not an item-creation side effect.)
        // ============================================================

        // 1) Ensure LM row (scrape on demand)
        var lm = await _lmRepo.GetByArticleNumberAsync(code, ct);
        if (lm is null)
        {
            var scraped = await _scraper.ScrapeByArticleNumbersAsync(new[] { code }, ct);
            if (scraped is null || scraped.Count == 0)
                return new LubesProvisioningResult("needs_review", code,
                    ReviewReason: "Liqui Moly returned no data for this article.");
            lm = scraped[0];
            await _lmRepo.UpsertAsync(lm, ct);
        }

        // 2) Rich description for both classifications
        var description = string.IsNullOrWhiteSpace(lm.Description)
            ? $"{code}-{lm.Name}."
            : $"{code}-{lm.Name}. {lm.Description}";
        var hint = lm.Category;

        // 3) Odoo category
        CategoryClassification catResult;
        try { catResult = await _classifier.ClassifyCategoryAsync(description, hint, ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Classifier (/classify) failed for {Code}", code);
            return new LubesProvisioningResult("needs_review", code,
                ReviewReason: "Classifier service unavailable.");
        }
        if (catResult.NeedsReview || string.IsNullOrEmpty(catResult.ExternalId))
            return new LubesProvisioningResult("needs_review", code,
                ReviewReason: $"Low confidence on Odoo category ({catResult.Confidence:F2}).",
                Candidates: catResult.Candidates);

        // 4) SAP family — three layers:
        //    Layer 1: deterministic business-rule overrides for known DGX blind spots (run BEFORE DGX,
        //             skipping the call entirely). These are high-certainty product-name rules.
        //    Layer 2: DGX /classify_family for everything else; accept confidence >= MinFamilyConfidence,
        //             overriding DGX's needs_review (with a WARN) rather than failing borderline items.
        FamilyClassification famResult;
        // Layer 3 audit values — persisted on NeonProducts so "what bypassed the gate" is a SQL query.
        double  familyConfidence;
        bool    familyNeedsReview;
        string? familyOverrideReason;
        var famOverride = MatchFamilyOverride(lm.Name);
        if (famOverride is { } ov)
        {
            famResult = new FamilyClassification
            {
                GroupCode = ov.Code, GroupName = ov.Name, Confidence = 1.0, NeedsReview = false,
            };
            familyConfidence     = 1.0;
            familyNeedsReview    = false;
            familyOverrideReason = $"layer1: {ov.Rule}";
            _logger.LogInformation(
                "SAP family override for {Code} '{Name}': {Group} {GroupName} [{Rule}] (DGX skipped)",
                code, lm.Name, ov.Code, ov.Name, ov.Rule);
        }
        else
        {
            try { famResult = await _classifier.ClassifyFamilyAsync(description, hint, ct); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Classifier (/classify_family) failed for {Code}", code);
                return new LubesProvisioningResult("needs_review", code,
                    ReviewReason: "Classifier service unavailable.");
            }

            if (famResult.GroupCode is null)
                return new LubesProvisioningResult("needs_review", code,
                    ReviewReason: $"No SAP family returned (confidence {famResult.Confidence:F2}).");

            familyConfidence     = famResult.Confidence;
            familyNeedsReview    = famResult.NeedsReview;
            familyOverrideReason = null;

            if (famResult.NeedsReview)
            {
                if (famResult.Confidence < MinFamilyConfidence)
                    return new LubesProvisioningResult("needs_review", code,
                        ReviewReason: $"Low confidence on SAP family ({famResult.Confidence:F2}).");

                // DGX flagged needs_review but confidence clears the bar — accept, and leave a trail.
                familyOverrideReason = $"layer2: dgx needs_review accepted at confidence {famResult.Confidence:F2}";
                _logger.LogWarning(
                    "SAP family needs_review OVERRIDDEN for {Code} '{Name}': accepted group {Group} {GroupName} " +
                    "at confidence {Conf:F2} (>= {Min:F2}). Spot-check this classification.",
                    code, lm.Name, famResult.GroupCode, famResult.GroupName, famResult.Confidence, MinFamilyConfidence);
            }
        }

        // 5) Pricing — keyed off the AUTHORITATIVE SAP/OITB group (famResult.GroupCode), not the noisy
        //    Odoo category, so two products in the same group always price identically (e.g. Pro-Line
        //    5151/5155 both → 112 → Workshop Pro-Line band, regardless of how DGX named their Odoo cat).
        //    Only if the SAP group has no dedicated band do we fall back to the scraped/DGX category.
        string pricingCat;
        try
        {
            var bandFromGroup = _pricing.TryPricingBandForSapGroup(famResult.GroupCode!.Value);
            if (bandFromGroup is not null)
            {
                pricingCat = _pricing.ResolvePricingCategory(bandFromGroup);
            }
            else
            {
                var pricingInput = !string.IsNullOrWhiteSpace(hint) ? hint : catResult.Name;
                pricingCat = _pricing.ResolvePricingCategory(pricingInput);
                _logger.LogInformation(
                    "Pricing for {Code}: SAP group {Group} has no band; fell back to category '{Cat}' → {Band}",
                    code, famResult.GroupCode, pricingInput, pricingCat);
            }
        }
        catch (InvalidOperationException ex)
        {
            return new LubesProvisioningResult("needs_review", code, ReviewReason: ex.Message);
        }
        var rate   = req.EurTzsRateOverride ?? _pricingSettings.EurTzsRate;
        var cifTzs = req.EurCost * rate;
        var prices = _pricing.ComputeNetPrices(cifTzs, pricingCat);

        var itemName = $"{code}-{lm.Name}";
        if (itemName.Length > 200) itemName = itemName.Substring(0, 200);

        // 6) PRE-FLIGHT VALIDATION — nothing has been written yet.
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(lm.Name))            missing.Add("ProductName");
        if (string.IsNullOrWhiteSpace(catResult.Name))     missing.Add("OdooCategoryName");
        if (string.IsNullOrWhiteSpace(catResult.ExternalId)) missing.Add("OdooCategoryExternalId");
        if (famResult.GroupCode is null)                   missing.Add("SapItemGroupCode");
        if (prices.Retail      <= 0m)                      missing.Add("RetailNetPrice");
        if (prices.Dealer      <= 0m)                      missing.Add("DealerNetPrice");
        if (prices.SuperDealer <= 0m)                      missing.Add("SuperDealerNetPrice");
        if (missing.Count > 0)
            return new LubesProvisioningResult("needs_review", code,
                ItemName: itemName,
                OdooCategoryName: catResult.Name,
                OdooCategoryExternalId: catResult.ExternalId,
                SapItemGroupCode: famResult.GroupCode,
                SapItemGroupName: famResult.GroupName,
                PricingCategory: pricingCat,
                CifCostTzs: cifTzs,
                ReviewReason: $"Required field(s) missing after classification/pricing: {string.Join(", ", missing)}.");

        // All required fields are present — build the desired SAP item state.
        var sapReq = new SapLubesItemRequest(
            ItemCode: code,
            ItemName: itemName,
            ItemsGroupCode: famResult.GroupCode!.Value,
            RetailNetPrice: prices.Retail,
            DealerNetPrice: prices.Dealer,
            SuperDealerNetPrice: prices.SuperDealer,
            OdooCategoryName: catResult.Name!);

        // 6b) Dry-run short-circuit: return everything we WOULD do, write nothing.
        if (req.DryRun)
            return BuildResult("dry_run", code, itemName, catResult, famResult, pricingCat, cifTzs, prices);

        // ============================================================
        // WRITE PHASE — SAP first (system of record), then Neon (idempotent).
        // ============================================================

        // 7) Decide create vs. recovery from the existing SAP state.
        SapItemSnapshot? snapshot;
        try { snapshot = await _sap.GetItemSnapshotAsync(code, ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SAP snapshot read failed for {Code}", code);
            return BuildResult("failed", code, itemName, catResult, famResult, pricingCat, cifTzs, prices,
                errorMessage: ex.Message);
        }

        string status;
        try
        {
            if (snapshot is null)
            {
                await _sap.CreateLubesItemAsync(sapReq);
                status = "created";
            }
            else
            {
                // Item already exists (possibly half-created) — fill only blank SAP fields.
                await _sap.UpdateBlankFieldsAsync(code, sapReq, ct);
                status = "recovered";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SAP write ({Mode}) failed for {Code}",
                snapshot is null ? "create" : "recover", code);
            return BuildResult("failed", code, itemName, catResult, famResult, pricingCat, cifTzs, prices,
                errorMessage: ex.Message);
        }

        // 8) Neon writes — idempotent; insert missing rows or refresh existing ones.
        try
        {
            await _productRepo.UpsertProductAsync(new NeonProductWrite(
                ItemCode: code,
                ItemName: itemName,
                ItemsGroupCode: famResult.GroupCode.Value,
                OdooCategoryExternalId: catResult.ExternalId!,
                OdooCategoryName: catResult.Name!,
                ListPrice: prices.Retail,
                SapStatus: "created",
                FamilyConfidence: (decimal)familyConfidence,
                FamilyNeedsReview: familyNeedsReview,
                FamilyOverrideReason: familyOverrideReason
            ), ct);

            await _productRepo.UpsertPricesAsync(
                code, prices.Retail, prices.Dealer, prices.SuperDealer, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SAP item {Code} write succeeded ({Status}) but Neon write failed; re-POST to heal.",
                code, status);
            return BuildResult("failed", code, itemName, catResult, famResult, pricingCat, cifTzs, prices,
                errorMessage: $"SAP item write succeeded ({status}) but Neon write failed: {ex.Message}");
        }

        // 9) Done
        return BuildResult(status, code, itemName, catResult, famResult, pricingCat, cifTzs, prices);
    }

    /// <summary>
    /// Builds a fully-populated result echoing everything computed by the data pipeline.
    /// Used for success ("created"/"recovered"), "dry_run", and post-pipeline failures —
    /// so a "failed" response still shows what we computed and would have written.
    /// </summary>
    private static LubesProvisioningResult BuildResult(
        string status, string code, string itemName,
        CategoryClassification catResult, FamilyClassification famResult,
        string pricingCat, decimal cifTzs, PriceTiers prices,
        string? errorMessage = null) =>
        new(
            Status: status,
            ItemCode: code,
            ItemName: itemName,
            OdooCategoryName: catResult.Name,
            OdooCategoryExternalId: catResult.ExternalId,
            SapItemGroupCode: famResult.GroupCode,
            SapItemGroupName: famResult.GroupName,
            PricingCategory: pricingCat,
            CifCostTzs: cifTzs,
            RetailNetPrice: prices.Retail,
            DealerNetPrice: prices.Dealer,
            SuperDealerNetPrice: prices.SuperDealer,
            ErrorMessage: errorMessage);
}
