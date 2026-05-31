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
/// Orchestrates end-to-end provisioning of one Liqui Moly item:
/// idempotency pre-check → ensure scraped data → classify Odoo category + SAP family →
/// price → (SAP create is master) → write Neon product + price-list rows.
/// SAP is the system of record: nothing is written to Neon unless SAP creation succeeds.
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

    public async Task<LubesProvisioningResult> ProvisionAsync(LubesProvisioningRequest req, CancellationToken ct)
    {
        var code = req.ArticleNumber?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(code))
            return new LubesProvisioningResult("failed", "", ErrorMessage: "ArticleNumber is required.");
        if (req.EurCost <= 0m)
            return new LubesProvisioningResult("failed", code, ErrorMessage: "EurCost must be > 0.");

        // 1) SAP existence pre-check (idempotency)
        if (await _sap.ItemExistsAsync(code))
            return new LubesProvisioningResult("exists", code);

        // 2) Ensure LM row (scrape on demand)
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

        // 3) Build the rich description used for both classifications
        var description = string.IsNullOrWhiteSpace(lm.Description)
            ? $"{code}-{lm.Name}."
            : $"{code}-{lm.Name}. {lm.Description}";
        var hint = lm.Category;

        // 4) Odoo category
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

        // 5) SAP family
        FamilyClassification famResult;
        try { famResult = await _classifier.ClassifyFamilyAsync(description, hint, ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Classifier (/classify_family) failed for {Code}", code);
            return new LubesProvisioningResult("needs_review", code,
                ReviewReason: "Classifier service unavailable.");
        }
        if (famResult.NeedsReview || famResult.GroupCode is null)
            return new LubesProvisioningResult("needs_review", code,
                ReviewReason: $"Low confidence on SAP family ({famResult.Confidence:F2}).");

        // 6) Pricing
        string pricingCat;
        try { pricingCat = _pricing.ResolvePricingCategory(hint); }
        catch (InvalidOperationException ex)
        {
            return new LubesProvisioningResult("needs_review", code,
                ReviewReason: ex.Message);
        }
        var rate   = req.EurTzsRateOverride ?? _pricingSettings.EurTzsRate;
        var cifTzs = req.EurCost * rate;
        var prices = _pricing.ComputeNetPrices(cifTzs, pricingCat);

        var itemName = $"{code}-{lm.Name}";
        if (itemName.Length > 200) itemName = itemName.Substring(0, 200);

        // 6b) Dry-run short-circuit: return everything we WOULD do, write nothing.
        if (req.DryRun)
            return new LubesProvisioningResult("dry_run", code,
                ItemName: itemName,
                OdooCategoryName: catResult.Name,
                OdooCategoryExternalId: catResult.ExternalId,
                SapItemGroupCode: famResult.GroupCode,
                SapItemGroupName: famResult.GroupName,
                PricingCategory: pricingCat,
                CifCostTzs: cifTzs,
                RetailNetPrice: prices.Retail,
                DealerNetPrice: prices.Dealer,
                SuperDealerNetPrice: prices.SuperDealer);

        // 7) SAP create (master — runs first)
        var sapReq = new SapLubesItemRequest(
            ItemCode: code,
            ItemName: itemName,
            ItemsGroupCode: famResult.GroupCode!.Value,
            RetailNetPrice: prices.Retail,
            DealerNetPrice: prices.Dealer,
            SuperDealerNetPrice: prices.SuperDealer,
            OdooCategoryName: catResult.Name ?? "");

        try { await _sap.CreateLubesItemAsync(sapReq); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SAP create failed for {Code}", code);
            return new LubesProvisioningResult("failed", code, ErrorMessage: ex.Message);
        }

        // 8) Neon writes (the Neon → Odoo automation picks these up)
        try
        {
            await _productRepo.UpsertProductAsync(new NeonProductWrite(
                ItemCode: code,
                ItemName: itemName,
                ItemsGroupCode: famResult.GroupCode.Value,
                OdooCategoryExternalId: catResult.ExternalId!,
                OdooCategoryName: catResult.Name!,
                ListPrice: prices.Retail,
                SapStatus: "created"
            ), ct);

            await _productRepo.UpsertPricesAsync(
                code, prices.Retail, prices.Dealer, prices.SuperDealer, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SAP item {Code} CREATED but Neon write failed; manual sync needed.", code);
            return new LubesProvisioningResult("failed", code,
                ErrorMessage: $"SAP item created but Neon write failed: {ex.Message}");
        }

        // 9) Done
        return new LubesProvisioningResult(
            Status: "created",
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
            SuperDealerNetPrice: prices.SuperDealer);
    }
}
