namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Autohub Phase B pricing knobs. Brand-band ratios and forex rates live in Neon
/// (pricing_brand_ratios / forex_rate); the only static constants are the retail markup and a
/// fallback wholesale ratio used when no brand band matches. Bound from the optional
/// "AutohubPricing" section. SAP price lists: PL01=Cost, PL03=Retail, PL05=Wholesale.
/// </summary>
public sealed class AutohubPricingSettings
{
    public const string SectionName = "AutohubPricing";

    /// <summary>PL03 (Retail) = Cost (TZS) × this. Default = 1.25.</summary>
    public decimal RetailMarkup { get; set; } = 1.25m;

    /// <summary>Retail→Wholesale ratio (PL05 = Retail × this) used when no pricing_brand_ratios band matches.</summary>
    public decimal DefaultWholesaleRatio { get; set; } = 0.85m;

    /// <summary>How long forex rates are cached in memory (they change infrequently).</summary>
    public int ForexCacheMinutes { get; set; } = 5;
}
