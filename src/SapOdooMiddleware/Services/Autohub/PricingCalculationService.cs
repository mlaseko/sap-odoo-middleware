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

/// <summary>
/// The full pricing trace for the operator review gate: the final prices PLUS every
/// intermediate the reviewer needs to see WHY (which brand row matched, which cost band,
/// whether the brand floor bound, and the rounding step applied to retail). Cost = CIF ×
/// CostMarkupMultiplier — the band keys on this post-markup cost, not the raw CIF.
/// </summary>
public sealed record PricingQuote(
    decimal Cif, decimal Cost, decimal Retail, decimal Wholesale,
    decimal Ratio, string RatioBrand, decimal BandMin, decimal? BandMax,
    bool FloorApplied, decimal? Floor, int RetailRoundingStep);

/// <summary>One line's outcome in a batch calculation: a quote, or why it could not be priced.</summary>
public sealed record PricingBatchResult(PricingQuote? Quote, string? Error);

public interface IPricingCalculationService
{
    /// <param name="supplierPriceTzs">Supplier unit price already converted to TZS (pre-markup).</param>
    /// <param name="brand">Supplier brand from extraction; matched to BORSEHUNG/DPA/OE/VIKA, else DEFAULT.</param>
    Task<PricingResult> CalculateAsync(decimal supplierPriceTzs, string brand, CancellationToken ct);

    /// <summary>Same calculation as <see cref="CalculateAsync"/>, returning the full trace.</summary>
    Task<PricingQuote> CalculateDetailedAsync(decimal supplierPriceTzs, string brand, CancellationToken ct);

    /// <summary>
    /// Batch form: the ratio, floor and rounding tables are fetched ONCE (three small
    /// queries) and every line is matched in memory — a 250-line invoice prices in one
    /// round-trip set instead of 3 per line. One result per input line, same order; a
    /// bad line yields an Error without failing the batch.
    /// </summary>
    Task<IReadOnlyList<PricingBatchResult>> CalculateBatchDetailedAsync(
        IReadOnlyList<(decimal SupplierPriceTzs, string? Brand)> lines, CancellationToken ct);

    /// <summary>
    /// The engine's wholesale rule for a given cost/retail pair — floor(retail − (retail −
    /// cost)/2) to the rounding step, nudged above cost. Used when a reviewer overrides PL03
    /// and PL05 must follow the same derivation Bulk Create uses.
    /// </summary>
    Task<decimal> DeriveWholesaleAsync(decimal cost, decimal retail, CancellationToken ct);
}

/// <summary>
/// Autohub pricing chain (D8), faithfully matching the operator's working JS calculator and the
/// existing Lubes band-ratio pattern (<see cref="Pricing.PricingCalculator"/>):
///   Cost      = supplierPrice × CostMarkupMultiplier (1.25)            → PL01
///   Retail    = ceil( MAX(Cost ÷ ratio, brand floor) )                  → PL03
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
        var q = await CalculateDetailedAsync(supplierPriceTzs, brand, ct);
        return new PricingResult(q.Cost, q.Retail, q.Wholesale, q.Ratio);
    }

    public async Task<PricingQuote> CalculateDetailedAsync(decimal supplierPriceTzs, string brand, CancellationToken ct)
    {
        var result = (await CalculateBatchDetailedAsync(new[] { (supplierPriceTzs, (string?)brand) }, ct))[0];
        if (result.Quote is null)
            throw new InvalidOperationException(result.Error ?? "Pricing failed.");
        return result.Quote;
    }

    public async Task<IReadOnlyList<PricingBatchResult>> CalculateBatchDetailedAsync(
        IReadOnlyList<(decimal SupplierPriceTzs, string? Brand)> lines, CancellationToken ct)
    {
        // The three config tables are tiny (dozens of rows) — fetch each once and match
        // every line in memory. This is what makes a 250-line preview sub-second.
        var bands = await _ratios.GetAllActiveBandsAsync(ct);
        var floors = await _ratios.GetAllActiveFloorsAsync(ct);
        var rules = await _rounding.GetRulesAsync(ct);

        var results = new List<PricingBatchResult>(lines.Count);
        foreach (var (cif, brand) in lines)
        {
            if (cif <= 0m)
            {
                results.Add(new PricingBatchResult(null, "Supplier price must be > 0."));
                continue;
            }

            // Markup is applied at cost ingestion; the band ratios then drive retail off this cost.
            var cost = Round2(cif * _settings.CostMarkupMultiplier);

            var key = string.IsNullOrWhiteSpace(brand) ? "DEFAULT" : brand.Trim();
            var ratioBrand = key;
            var band = MatchBand(bands, key, cost);
            if (band is null && !string.Equals(key, "DEFAULT", StringComparison.OrdinalIgnoreCase))
            {
                ratioBrand = "DEFAULT";
                band = MatchBand(bands, "DEFAULT", cost);
            }
            if (band is null)
            {
                results.Add(new PricingBatchResult(null,
                    $"No pricing_brand_ratios band (incl. DEFAULT) covers cost {cost} TZS — check the seed."));
                continue;
            }

            // Minimum selling price per brand (pricing_brand_floors), applied AFTER the
            // ratio and BEFORE rounding: cost-plus breaks down on cheap parts, where the
            // settled price is a floor, not a markup. Keyed by the actual brand (no
            // DEFAULT fallback), so it applies even when the ratio fell back to DEFAULT.
            var retailRaw = cost / band.Ratio;
            decimal? floor = floors.TryGetValue(key, out var minSell) ? minSell : null;
            var floorApplied = floor is { } f && retailRaw < f;
            if (floorApplied)
                retailRaw = floor!.Value;

            var roundingStep = IncrementFor(retailRaw, rules);
            var retail = RoundCeiling(retailRaw, rules);
            var wholesale = DeriveWholesale(cost, retail, rules);

            results.Add(new PricingBatchResult(new PricingQuote(
                Cif: cif, Cost: cost, Retail: retail, Wholesale: wholesale,
                Ratio: band.Ratio, RatioBrand: ratioBrand, BandMin: band.BandMin, BandMax: band.BandMax,
                FloorApplied: floorApplied, Floor: floor, RetailRoundingStep: roundingStep), null));
        }
        return results;
    }

    /// <summary>The tightest active band for (brand, cost) — same rule as the SQL match:
    /// BandMin ≤ cost &lt; BandMax (NULL = unbounded), highest BandMin wins.</summary>
    private static BrandRatioRow? MatchBand(IReadOnlyList<BrandRatioRow> bands, string brand, decimal cost)
    {
        BrandRatioRow? best = null;
        foreach (var b in bands)
        {
            if (!string.Equals(b.Brand, brand, StringComparison.OrdinalIgnoreCase)) continue;
            if (b.BandMin > cost || (b.BandMax is { } max && max <= cost)) continue;
            if (best is null || b.BandMin > best.BandMin) best = b;
        }
        return best;
    }

    public async Task<decimal> DeriveWholesaleAsync(decimal cost, decimal retail, CancellationToken ct)
        => DeriveWholesale(cost, retail, await _rounding.GetRulesAsync(ct));

    private decimal DeriveWholesale(decimal cost, decimal retail, IReadOnlyList<RoundingRule> rules)
    {
        var wholesale = RoundFloor(retail - (retail - cost) / 2m, rules);
        // Wholesale must clear cost; if rounding pulled it to/below cost, nudge it above.
        return wholesale <= cost ? cost + _settings.WholesaleFloorOverCost : wholesale;
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
