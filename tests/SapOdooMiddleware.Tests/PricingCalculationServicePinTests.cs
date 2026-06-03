using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Golden pinning tests for the Autohub pricing chain, locked to outputs validated against the live
/// pricing_brand_ratios / pricing_rounding_rules seed in Neon (the operator ran the equivalent SQL
/// and confirmed all five). Uses fakes that mirror the real seeded ratios + the 7 rounding rules, so
/// these exercise the actual band selection, ceiling-retail and floor-wholesale-midpoint logic.
///
/// The SQL test cases express the *post-markup* cost; the service input is the supplier price, so
/// supplierPrice = cost / 1.25 (CostMarkupMultiplier). All five divide exactly.
/// </summary>
public class PricingCalculationServicePinTests
{
    // Mirror of the seeded pricing_brand_ratios (5 brands × 8 cost bands).
    private sealed class SeededRatioRepo : IPricingBrandRatioRepository
    {
        private static (decimal Min, decimal? Max, decimal Ratio)[] Bands(
            decimal b0, decimal b1, decimal b2, decimal b3, decimal b4, decimal b5, decimal b6, decimal b7) => new[]
        {
            (0m,        (decimal?)10000m,   b0),
            (10000m,    (decimal?)25000m,   b1),
            (25000m,    (decimal?)50000m,   b2),
            (50000m,    (decimal?)100000m,  b3),
            (100000m,   (decimal?)200000m,  b4),
            (200000m,   (decimal?)500000m,  b5),
            (500000m,   (decimal?)1000000m, b6),
            (1000000m,  (decimal?)null,     b7),
        };

        private static readonly Dictionary<string, (decimal Min, decimal? Max, decimal Ratio)[]> Table =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["DEFAULT"]   = Bands(0.190m, 0.240m, 0.270m, 0.300m, 0.320m, 0.345m, 0.370m, 0.390m),
                ["BORSEHUNG"] = Bands(0.201m, 0.252m, 0.295m, 0.321m, 0.338m, 0.365m, 0.375m, 0.392m),
                ["DPA"]       = Bands(0.185m, 0.221m, 0.252m, 0.285m, 0.312m, 0.338m, 0.362m, 0.384m),
                ["OE"]        = Bands(0.165m, 0.214m, 0.258m, 0.291m, 0.319m, 0.346m, 0.371m, 0.395m),
                ["VIKA"]      = Bands(0.188m, 0.235m, 0.276m, 0.308m, 0.332m, 0.352m, 0.369m, 0.388m),
            };

        public Task<decimal?> GetCostToRetailRatioAsync(string brand, decimal costTzs, CancellationToken ct)
        {
            if (!Table.TryGetValue(brand, out var bands))
                return Task.FromResult<decimal?>(null);   // unknown brand → service falls back to DEFAULT
            foreach (var (min, max, ratio) in bands)
                if (costTzs >= min && (max is null || costTzs < max))
                    return Task.FromResult<decimal?>(ratio);
            return Task.FromResult<decimal?>(null);
        }
    }

    // The 7 seeded magnitude-based rounding rules.
    private sealed class SeededRoundingRepo : IPricingRoundingRuleRepository
    {
        public Task<IReadOnlyList<RoundingRule>> GetRulesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RoundingRule>>(new RoundingRule[]
            {
                new(0,        10000,    500),
                new(10000,    50000,    1000),
                new(50000,    200000,   5000),
                new(200000,   500000,   10000),
                new(500000,   1000000,  25000),
                new(1000000,  5000000,  50000),
                new(5000000,  null,     100000),
            });
    }

    // brand, supplierPrice (= cost/1.25), expectedCost, expectedRetail, expectedWholesale.
    [Theory]
    [InlineData("VIKA",        54000,   67500,   220000,  140000)]
    [InlineData("BORSEHUNG",   40000,   50000,   160000,  105000)]
    [InlineData("DPA",          4000,    5000,    28000,   16000)]
    [InlineData("OE",        1200000, 1500000,  3800000, 2650000)]
    [InlineData("DEFAULT",     20000,   25000,    95000,   60000)]
    public async Task Calculate_MatchesDbValidatedExpectations(
        string brand, int supplierPrice, int expectedCost, int expectedRetail, int expectedWholesale)
    {
        var svc = new PricingCalculationService(
            new SeededRatioRepo(),
            new SeededRoundingRepo(),
            Options.Create(new AutohubPricingSettings()));   // CostMarkupMultiplier defaults to 1.25

        var r = await svc.CalculateAsync(supplierPrice, brand, CancellationToken.None);

        Assert.Equal((decimal)expectedCost, r.Cost);
        Assert.Equal((decimal)expectedRetail, r.Retail);
        Assert.Equal((decimal)expectedWholesale, r.Wholesale);
        Assert.True(r.Wholesale > r.Cost);
    }
}
