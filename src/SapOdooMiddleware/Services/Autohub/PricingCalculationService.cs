using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>Result of the pricing chain, all in TZS, rounded to 2dp.</summary>
public sealed record PricingResult(decimal Pl01, decimal Pl03, decimal Pl05, decimal RatioUsed);

public interface IPricingCalculationService
{
    Task<PricingResult> CalculateAsync(decimal buyingPriceTzs, string brand, CancellationToken ct);
}

/// <summary>
/// Implements decision D8's pricing chain:
///   PL01 (Retail)    = BuyingPrice(TZS) × RetailMarkup (default 1.25)
///   PL03 (Wholesale) = PL01 × brand-band ratio (from pricing_brand_ratios; falls back to default)
///   PL05 (Midpoint)  = (PL01 + PL03) / 2
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
        var pl01 = Round(buyingPriceTzs * _settings.RetailMarkup);

        var ratio = await _ratios.GetRatioAsync(brand, pl01, ct) ?? _settings.DefaultPl01ToPl03;
        var pl03 = Round(pl01 * ratio);

        var pl05 = Round((pl01 + pl03) / 2m);

        return new PricingResult(pl01, pl03, pl05, ratio);
    }

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
