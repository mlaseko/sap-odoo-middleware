namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Autohub Phase B pricing knobs. Brand-band ratios (PL01→PL03) and forex rates live in Neon
/// (pricing_brand_ratios / forex_rate); the only static constants are the retail markup and a
/// fallback ratio used when no brand band matches. Bound from the optional "AutohubPricing"
/// section; the defaults match decision D8.
/// </summary>
public sealed class AutohubPricingSettings
{
    public const string SectionName = "AutohubPricing";

    /// <summary>PL01 (Retail) = Buying Price (TZS) × this. D8 default = 1.25.</summary>
    public decimal RetailMarkup { get; set; } = 1.25m;

    /// <summary>PL01→PL03 ratio used when no pricing_brand_ratios band matches the brand/price.</summary>
    public decimal DefaultPl01ToPl03 { get; set; } = 0.85m;

    /// <summary>How long forex rates are cached in memory (they change infrequently).</summary>
    public int ForexCacheMinutes { get; set; } = 5;
}
