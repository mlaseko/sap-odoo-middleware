namespace SapOdooMiddleware.ItemProvisioning;

/// <summary>
/// Request to provision one Liqui Moly item end-to-end. Field names are bound from
/// snake_case JSON (repo-wide policy): article_number, eur_cost, eur_tzs_rate_override, dry_run.
/// </summary>
public record LubesProvisioningRequest(
    string ArticleNumber,
    decimal EurCost,
    decimal? EurTzsRateOverride = null,
    bool DryRun = false);
