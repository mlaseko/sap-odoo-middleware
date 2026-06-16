namespace SapOdooMiddleware.ItemProvisioning;

/// <summary>
/// Request to provision one Liqui Moly item end-to-end. Field names are bound from
/// snake_case JSON (repo-wide policy): article_number, eur_cost, eur_tzs_rate_override, dry_run.
/// </summary>
public record LubesProvisioningRequest(
    string ArticleNumber,
    decimal EurCost,
    decimal? EurTzsRateOverride = null,
    bool DryRun = false,
    // Supplier line description from the invoice. Used only for brand routing: a name starting with
    // "Meguin" (an LM subsidiary, invoiced under LM) is scraped from meguin.com instead of liqui-moly.com.
    string? SupplierName = null,
    // Manual Odoo-category override (reviewer-assigned). When both are set, provisioning uses this category
    // instead of calling the DGX classifier — the resolution path for low-confidence category failures.
    string? OdooCategoryOverrideExternalId = null,
    string? OdooCategoryOverrideName = null,
    // Reviewer chose to accept DGX's low-confidence Odoo category as-is (instead of failing to manual
    // review). Used by the "accept low confidence" review action; only takes effect when DGX returns a
    // category (non-empty ExternalId).
    bool AcceptLowConfidenceCategory = false);
