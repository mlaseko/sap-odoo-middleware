using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Result of the pricing chain, all in TZS, rounded to 2dp. Field names map directly to the
/// Autohub SAP price lists (confirmed by the operator): Cost→PL01, Retail→PL03, Wholesale→PL05.
/// There are no dealer / super-dealer tiers in Molas Autohub.
/// </summary>
public sealed record PricingResult(decimal Cost, decimal Retail, decimal Wholesale, decimal RatioUsed);

public interface IPricingCalculationService
{
    Task<PricingResult> CalculateAsync(decimal buyingPriceTzs, string brand, CancellationToken ct);
}

/// <summary>
/// Implements the Autohub pricing chain (D8), mapped to the live SAP price lists:
///   PL01 (Cost)      = BuyingPrice(TZS)                    — the landed cost, stored as-is
///   PL03 (Retail)    = Cost × RetailMarkup (default 1.25)  — the retail selling price
///   PL05 (Wholesale) = Retail × brand-band ratio           — wholesale, a discount off retail
/// The brand-band ratio comes from pricing_brand_ratios keyed on the retail (selling) price band,
/// falling back to <see cref="AutohubPricingSettings.DefaultWholesaleRatio"/> when no band matches.
/// </summary>
public sealed class PricingCalculationService : IPricingCalculationService
{
    private readonly IPricingBrandRatioRepository _ratios;
    private readonly AutohubPricingSettings _settings;

    public PricingCalculationService(IPricingBrandRatioRepository ratios, IOptions<AutohubPricingSettings> settings)
    {
        _ratios = ratios;
        _settings = settings.Value;
    }

    public async Task<PricingResult> CalculateAsync(decimal buyingPriceTzs, string brand, CancellationToken ct)
    {
        var cost = Round(buyingPriceTzs);
        var retail = Round(buyingPriceTzs * _settings.RetailMarkup);

        // Bands are defined on the selling (retail) price; the ratio is the retail→wholesale factor.
        var ratio = await _ratios.GetRatioAsync(brand, retail, ct) ?? _settings.DefaultWholesaleRatio;
        var wholesale = Round(retail * ratio);

        return new PricingResult(cost, retail, wholesale, ratio);
    }

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
