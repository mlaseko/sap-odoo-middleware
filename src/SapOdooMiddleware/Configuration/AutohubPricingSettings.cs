namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Autohub Phase B pricing knobs. The brand×cost-band ratios, rounding rules and forex rates all
/// live in Neon (pricing_brand_ratios / pricing_rounding_rules / forex_rate) so the operator can
/// tune them without a redeploy. The only static constant here is the cost markup applied at
/// ingestion. Bound from the optional "AutohubPricing" section. SAP price lists: PL01=Cost,
/// PL03=Retail, PL05=Wholesale.
///
/// Pricing model (mirrors the operator's working JS calculator):
///   Cost      = supplierPrice(TZS) × CostMarkupMultiplier   → PL01
///   Retail    = ceil( Cost ÷ ratio )                         → PL03   (ratio per brand×cost-band)
///   Wholesale = floor( Retail − (Retail − Cost) / 2 )        → PL05   (margin midpoint)
/// Ceiling/floor round to a magnitude-dependent increment from pricing_rounding_rules.
/// </summary>
public sealed class AutohubPricingSettings
{
    public const string SectionName = "AutohubPricing";

    /// <summary>
    /// Markup applied at cost ingestion: Cost (PL01) = supplierPrice(TZS) × this. Default = 1.25.
    /// (Retail no longer carries the markup — it comes from the cost÷ratio bands.)
    /// </summary>
    public decimal CostMarkupMultiplier { get; set; } = 1.25m;

    /// <summary>
    /// Wholesale must stay above Cost; when the midpoint formula rounds at/below Cost, Wholesale
    /// falls back to Cost + this. Default = 1000 (TZS).
    /// </summary>
    public decimal WholesaleFloorOverCost { get; set; } = 1000m;

    /// <summary>How long forex rates are cached in memory (they change infrequently).</summary>
    public int ForexCacheMinutes { get; set; } = 5;
}
