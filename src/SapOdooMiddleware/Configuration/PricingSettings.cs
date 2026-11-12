namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Pricing settings for Item Provisioning.
/// </summary>
public class PricingSettings
{
    public const string SectionName = "Pricing";

    /// <summary>Default EUR→TZS conversion rate; overridable per-request. The
    /// runtime-editable Neon override (pricing_settings) takes precedence when set.</summary>
    public decimal EurTzsRate { get; set; } = 2950m;

    /// <summary>
    /// Lubes reprice preview WARNS (never blocks) when a price list's net (excl-VAT) price is less
    /// than this percentage above the TZS cost. 0 = warn only when a price is below cost.
    /// </summary>
    public decimal MarginWarnPct { get; set; } = 5m;

    /// <summary>
    /// High-cost cap (Lubes): costs above this many TZS price as "the price at this cost + the extra cost
    /// × the Cap* multiples". Default 1,260,000 ≈ EUR 400 at 3,150. 0 disables the cap.
    /// </summary>
    public decimal CapStartCostTzs { get; set; } = 1_260_000m;

    /// <summary>Retail multiple (VAT incl.) applied to the cost above <see cref="CapStartCostTzs"/>.</summary>
    public decimal CapRetailMultiple { get; set; } = 1.40m;

    /// <summary>Dealer multiple (VAT incl.) applied to the cost above <see cref="CapStartCostTzs"/>.</summary>
    public decimal CapDealerMultiple { get; set; } = 1.35m;

    /// <summary>Super Dealer multiple (VAT incl.) applied to the cost above <see cref="CapStartCostTzs"/>.</summary>
    public decimal CapSuperDealerMultiple { get; set; } = 1.28m;

    /// <summary>
    /// Maps price tiers to Odoo pricelist NAMES so a reprice can update them via
    /// JSON-RPC. Keys: Retail, Dealer, SuperDealer, Maasai. An unmapped tier is
    /// skipped (Retail additionally always updates the product's list_price).
    /// Example config: "OdooPricelistNames": { "Dealer": "Dealer", "SuperDealer": "Super Dealer", "Maasai": "Maasai" }
    /// </summary>
    public Dictionary<string, string> OdooPricelistNames { get; set; } = new();
}
