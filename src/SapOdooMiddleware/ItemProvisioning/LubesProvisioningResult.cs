namespace SapOdooMiddleware.ItemProvisioning;

public record LubesProvisioningResult(
    string  Status,                              // "created" | "recovered" | "needs_review" | "failed" | "dry_run"
    string  ItemCode,
    string? ItemName                = null,
    string? OdooCategoryName        = null,
    string? OdooCategoryExternalId  = null,
    int?    SapItemGroupCode        = null,
    string? SapItemGroupName        = null,
    string? PricingCategory         = null,
    decimal? CifCostTzs             = null,
    decimal? RetailNetPrice         = null,
    decimal? DealerNetPrice         = null,
    decimal? SuperDealerNetPrice    = null,
    string? ReviewReason            = null,
    List<string>? Candidates        = null,
    string? ErrorMessage            = null);
