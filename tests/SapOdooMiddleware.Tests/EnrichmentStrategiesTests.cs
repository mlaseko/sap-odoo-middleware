using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

public class EnrichmentStrategiesTests
{
    [Theory]
    [InlineData("tecdoc_direct",        "enrichment_direct_auto_match")]
    [InlineData("borrowed_oem_bridge",  "borrowed_oem_bridge_auto_match")]
    [InlineData("germax_local",         "germax_local_auto_match")]
    [InlineData("rapidapi_tecdoc_live", "rapidapi_tecdoc_live_auto_match")]
    [InlineData("something_else",       "enrichment_auto_match")]
    public void ResolveSourceAutoMatch_ReturnsExpected(string source, string expected)
        => Assert.Equal(expected, EnrichmentStrategies.ResolveSourceAutoMatch(source));

    [Theory]
    [InlineData("tecdoc_direct",        "enrichment_direct")]
    [InlineData("borrowed_oem_bridge",  "borrowed_oem_bridge_create_new")]
    [InlineData("germax_local",         "germax_local_create_new")]
    [InlineData("rapidapi_tecdoc_live", "rapidapi_tecdoc_live_create_new")]
    public void ResolveSourceCreateNew_ReturnsExpected(string source, string expected)
        => Assert.Equal(expected, EnrichmentStrategies.ResolveSourceCreateNew(source));

    [Theory]
    [InlineData("tecdoc_direct",        "tecdoc_direct_cross_supplier_create_new")]
    [InlineData("borrowed_oem_bridge",  "borrowed_cross_supplier_create_new")]
    [InlineData("germax_local",         "germax_cross_supplier_create_new")]
    [InlineData("rapidapi_tecdoc_live", "rapidapi_cross_supplier_create_new")]
    public void ResolveSourceCrossSupplier_ReturnsExpected(string source, string expected)
        => Assert.Equal(expected, EnrichmentStrategies.ResolveSourceCrossSupplier(source));

    [Theory]
    [InlineData("tecdoc_direct",        "tecdoc_direct_needs_confirmation")]
    [InlineData("borrowed_oem_bridge",  "borrowed_oem_bridge_needs_confirmation")]
    [InlineData("germax_local",         "germax_needs_confirmation")]
    [InlineData("rapidapi_tecdoc_live", "rapidapi_needs_confirmation")]
    [InlineData("something_else",       "vehicle_group_brand_needs_confirmation")]
    public void ResolveSourceNeedsConfirmation_ReturnsExpected(string source, string expected)
        => Assert.Equal(expected, EnrichmentStrategies.ResolveSourceNeedsConfirmation(source));

    [Theory]
    [InlineData("borrowed_cross_supplier_create_new", true)]
    [InlineData("germax_cross_supplier_create_new", true)]
    [InlineData("rapidapi_cross_supplier_create_new", true)]
    [InlineData("tecdoc_direct_cross_supplier_create_new", true)]
    [InlineData("borrowed_oem_bridge_auto_match", false)]
    [InlineData("germax_local_create_new", false)]
    [InlineData("tier1_oem", false)]
    [InlineData(null, false)]
    public void IsCrossSupplierStrategy_ReturnsExpected(string? strategy, bool expected)
        => Assert.Equal(expected, EnrichmentStrategies.IsCrossSupplierStrategy(strategy));

    [Theory]
    [InlineData("borrowed_cross_supplier_create_new", "molas_borrowed_cross_supplier")]
    [InlineData("germax_cross_supplier_create_new", "molas_germax_cross_supplier")]
    [InlineData("rapidapi_cross_supplier_create_new", "molas_rapidapi_cross_supplier")]
    [InlineData("tecdoc_direct_cross_supplier_create_new", "molas_tecdoc_cross_supplier")]
    [InlineData("weird", "molas_cross_supplier")]
    public void ResolveOwnIdentitySource_ReturnsExpected(string strategy, string expected)
        => Assert.Equal(expected, EnrichmentStrategies.ResolveOwnIdentitySource(strategy));
}
