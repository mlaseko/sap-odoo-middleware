namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Maps DGX <c>source</c> values to the persisted <c>MatchStrategy</c> labels, and recognises the
/// cross-supplier strategies that require minting an own-identity oitm row (Slice 2). Pure functions,
/// shared by <see cref="EnrichmentResultRouter"/> (routing) and the provisioning service (linking).
///
/// DGX sources: tecdoc_direct · borrowed_oem_bridge · germax_local · rapidapi_tecdoc_live (+ unmatched).
/// </summary>
public static class EnrichmentStrategies
{
    /// <summary>Same-supplier donor already a SAP item → auto-match.</summary>
    public static string ResolveSourceAutoMatch(string? source) => source switch
    {
        "tecdoc_direct"        => "enrichment_direct_auto_match",
        "borrowed_oem_bridge"  => "borrowed_oem_bridge_auto_match",
        "germax_local"         => "germax_local_auto_match",
        "rapidapi_tecdoc_live" => "rapidapi_tecdoc_live_auto_match",
        _                      => "enrichment_auto_match",
    };

    /// <summary>Donor not yet a SAP item (same/unknown supplier) → create-new.</summary>
    public static string ResolveSourceCreateNew(string? source) => source switch
    {
        "tecdoc_direct"        => "enrichment_direct",
        "borrowed_oem_bridge"  => "borrowed_oem_bridge_create_new",
        "germax_local"         => "germax_local_create_new",
        "rapidapi_tecdoc_live" => "rapidapi_tecdoc_live_create_new",
        _                      => "enrichment_create_new",
    };

    /// <summary>Donor is a SAP item under a DIFFERENT supplier → create-new under our identity, borrow enrichment.</summary>
    public static string ResolveSourceCrossSupplier(string? source) => source switch
    {
        "tecdoc_direct"        => "tecdoc_direct_cross_supplier_create_new",
        "borrowed_oem_bridge"  => "borrowed_cross_supplier_create_new",   // existing Slice 1.6 value
        "germax_local"         => "germax_cross_supplier_create_new",
        "rapidapi_tecdoc_live" => "rapidapi_cross_supplier_create_new",
        _                      => "enrichment_cross_supplier_create_new",
    };

    /// <summary>Invoice brand is a vehicle-group code (VAG/BMW/…) or missing → operator confirms (use donor / create-new).</summary>
    public static string ResolveSourceNeedsConfirmation(string? source) => source switch
    {
        "tecdoc_direct"        => "tecdoc_direct_needs_confirmation",
        "borrowed_oem_bridge"  => "borrowed_oem_bridge_needs_confirmation",
        "germax_local"         => "germax_needs_confirmation",
        "rapidapi_tecdoc_live" => "rapidapi_needs_confirmation",
        _                      => "vehicle_group_brand_needs_confirmation",
    };

    /// <summary>True for any cross-supplier create-new strategy (needs an own-identity oitm row, not a donor write).</summary>
    public static bool IsCrossSupplierStrategy(string? strategy) =>
        strategy is "borrowed_cross_supplier_create_new"
                 or "germax_cross_supplier_create_new"
                 or "rapidapi_cross_supplier_create_new"
                 or "tecdoc_direct_cross_supplier_create_new"
                 or "enrichment_cross_supplier_create_new";

    /// <summary>The <c>oitm.source</c> tag for a middleware-minted own-identity row (molas_* prefix = our write).</summary>
    public static string ResolveOwnIdentitySource(string? strategy) => strategy switch
    {
        "borrowed_cross_supplier_create_new"      => "molas_borrowed_cross_supplier",
        "germax_cross_supplier_create_new"        => "molas_germax_cross_supplier",
        "rapidapi_cross_supplier_create_new"      => "molas_rapidapi_cross_supplier",
        "tecdoc_direct_cross_supplier_create_new" => "molas_tecdoc_cross_supplier",
        _                                         => "molas_cross_supplier",
    };
}
