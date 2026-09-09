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
    /// Maps price tiers to Odoo pricelist NAMES so a reprice can update them via
    /// JSON-RPC. Keys: Retail, Dealer, SuperDealer, Maasai. An unmapped tier is
    /// skipped (Retail additionally always updates the product's list_price).
    /// Example config: "OdooPricelistNames": { "Dealer": "Dealer", "SuperDealer": "Super Dealer", "Maasai": "Maasai" }
    /// </summary>
    public Dictionary<string, string> OdooPricelistNames { get; set; } = new();
}
