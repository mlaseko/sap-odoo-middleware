using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Result of the pricing chain, all in TZS. Field names map directly to the Autohub SAP price lists
/// (confirmed by the operator): Cost→PL01, Retail→PL03, Wholesale→PL05. There are no dealer /
/// super-dealer tiers in Molas Autohub. RatioUsed is the cost→retail ratio that produced Retail.
/// </summary>
public sealed record PricingResult(decimal Cost, decimal Retail, decimal Wholesale, decimal RatioUsed);

public interface IPricingCalculationService
{
    /// <param name="supplierPriceTzs">Supplier unit price already converted to TZS (pre-markup).</param>
    /// <param name="brand">Supplier brand from extraction; matched to BORSEHUNG/DPA/OE/VIKA, else DEFAULT.</param>
    Task<PricingResult> CalculateAsync(decimal supplierPriceTzs, string brand, CancellationToken ct);
}

/// <summary>
/// Autohub pricing chain (D8), faithfully matching the operator's working JS calculator and the
/// existing Lubes band-ratio pattern (<see cref="Pricing.PricingCalculator"/>):
///   Cost      = supplierPrice × CostMarkupMultiplier (1.25)            → PL01
///   Retail    = ceil( Cost ÷ ratio )                                    → PL03
///   Wholesale = floor( Retail − (Retail − Cost) / 2 ), &gt; Cost         → PL05
/// The ratio is the brand×cost-band value from pricing_brand_ratios (case-insensitive brand,
/// falling back to 'DEFAULT'). Ceiling/floor round to a magnitude-dependent increment from
/// pricing_rounding_rules. Both tables are operator-tunable in Neon without a redeploy.
/// </summary>
public sealed class PricingCalculationService : IPricingCalculationService
{
    private readonly IPricingBrandRatioRepository _ratios;
    private readonly IPricingRoundingRuleRepository _rounding;
    private readonly AutohubPricingSettings _settings;

    public PricingCalculationService(
        IPricingBrandRatioRepository ratios,
        IPricingRoundingRuleRepository rounding,
        IOptions<AutohubPricingSettings> settings)
    {
        _ratios = ratios;
        _rounding = rounding;
        _settings = settings.Value;
    }

    public async Task<PricingResult> CalculateAsync(decimal supplierPriceTzs, string brand, CancellationToken ct)
    {
        if (supplierPriceTzs <= 0m)
            throw new ArgumentOutOfRangeException(nameof(supplierPriceTzs), "Supplier price must be > 0.");

        // Markup is applied at cost ingestion; the band ratios then drive retail off this cost.
        var cost = Round2(supplierPriceTzs * _settings.CostMarkupMultiplier);

        var key = string.IsNullOrWhiteSpace(brand) ? "DEFAULT" : brand.Trim();
        var ratio = await _ratios.GetCostToRetailRatioAsync(key, cost, ct)
                    ?? await _ratios.GetCostToRetailRatioAsync("DEFAULT", cost, ct)
                    ?? throw new InvalidOperationException(
                        $"No pricing_brand_ratios band (incl. DEFAULT) covers cost {cost} TZS — check the seed.");

        var rules = await _rounding.GetRulesAsync(ct);

        var retail = RoundCeiling(cost / ratio, rules);
        var wholesaleRaw = retail - (retail - cost) / 2m;
        var wholesale = RoundFloor(wholesaleRaw, rules);

        // Wholesale must clear cost; if rounding pulled it to/below cost, nudge it above.
        if (wholesale <= cost)
            wholesale = cost + _settings.WholesaleFloorOverCost;

        return new PricingResult(cost, retail, wholesale, ratio);
    }

    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static int IncrementFor(decimal value, IReadOnlyList<RoundingRule> rules)
    {
        foreach (var r in rules)
            if (value >= r.MinPrice && (r.MaxPrice is null || value < r.MaxPrice))
                return r.RoundTo;
        // No rule matched (empty table or value below the first floor) — round to the nearest 1.
        return rules.Count > 0 ? rules[rules.Count - 1].RoundTo : 1;
    }

    private static decimal RoundCeiling(decimal value, IReadOnlyList<RoundingRule> rules)
    {
        var inc = IncrementFor(value, rules);
        return Math.Ceiling(value / inc) * inc;
    }

    private static decimal RoundFloor(decimal value, IReadOnlyList<RoundingRule> rules)
    {
        var inc = IncrementFor(value, rules);
        return Math.Floor(value / inc) * inc;
    }
}
