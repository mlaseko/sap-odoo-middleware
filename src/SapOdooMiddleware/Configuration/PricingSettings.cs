namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Pricing settings for Item Provisioning.
/// </summary>
public class PricingSettings
{
    public const string SectionName = "Pricing";

    /// <summary>Default EUR→TZS conversion rate; overridable per-request.</summary>
    public decimal EurTzsRate { get; set; } = 2950m;
}
