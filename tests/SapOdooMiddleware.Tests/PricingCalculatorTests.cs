using SapOdooMiddleware.Pricing;

namespace SapOdooMiddleware.Tests;

public class PricingCalculatorTests
{
    private readonly PricingCalculator _calc = new();

    [Fact]
    public void ComputeNetPrices_Additives_12324_matches_reference_spreadsheet_row()
    {
        // Reference: row 8380 in Molas_Bulk_Prices_2026-05-31.xlsx (Additives, cost 12324).
        // NET (excl-VAT) = Incl / 1.18, where Incl values are 50,000 / 45,000 / 31,000.
        var prices = _calc.ComputeNetPrices(12324m, "Additives");

        Assert.Equal(42372.88m, prices.Retail, 2);
        Assert.Equal(38135.59m, prices.Dealer, 2);
        Assert.Equal(26271.19m, prices.SuperDealer, 2);
    }

    [Fact]
    public void ComputeNetPrices_enforces_super_dealer_lt_dealer_lt_retail()
    {
        var prices = _calc.ComputeNetPrices(50000m, "Engine Oils");

        Assert.True(prices.SuperDealer < prices.Dealer);
        Assert.True(prices.Dealer < prices.Retail);
    }

    [Fact]
    public void ComputeNetPrices_rejects_non_positive_cost()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _calc.ComputeNetPrices(0m, "Additives"));
    }

    [Theory]
    [InlineData("gear oils", "Gear Oils & Transmission Fluids")]
    [InlineData("Engine Oils", "Engine Oils")]
    [InlineData("oils", "Engine Oils")]
    [InlineData("service products", "Service")]
    public void ResolvePricingCategory_maps_aliases(string input, string expected)
    {
        Assert.Equal(expected, _calc.ResolvePricingCategory(input));
    }

    [Fact]
    public void ResolvePricingCategory_throws_for_unknown_category()
    {
        Assert.Throws<InvalidOperationException>(() => _calc.ResolvePricingCategory("Nonexistent Category"));
    }
}
