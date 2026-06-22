using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

public class PricingCalculationServiceTests
{
    // Returns _brandRatio for any brand except "DEFAULT", which returns _defaultRatio. A null means
    // "no band" so the service falls back to DEFAULT (mirrors the real repo's null-on-miss contract).
    private sealed class FakeRatioRepo : IPricingBrandRatioRepository
    {
        private readonly decimal? _brandRatio;
        private readonly decimal? _defaultRatio;
        public FakeRatioRepo(decimal? brandRatio, decimal? defaultRatio = null)
        {
            _brandRatio = brandRatio;
            _defaultRatio = defaultRatio;
        }
        public Task<decimal?> GetCostToRetailRatioAsync(string brand, decimal costTzs, CancellationToken ct)
            => Task.FromResult(string.Equals(brand, "DEFAULT", StringComparison.OrdinalIgnoreCase) ? _defaultRatio : _brandRatio);
    }

    // The real 7 magnitude-based rounding rules.
    private sealed class FakeRoundingRepo : IPricingRoundingRuleRepository
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

    private static PricingCalculationService Build(decimal? brandRatio, decimal? defaultRatio = null, decimal markup = 1.25m)
        => new(new FakeRatioRepo(brandRatio, defaultRatio),
               new FakeRoundingRepo(),
               Options.Create(new AutohubPricingSettings { CostMarkupMultiplier = markup }));

    [Fact]
    public async Task Calculate_AppliesMarkupAtCost_ThenCostDivRatio_WithMidpointWholesale()
    {
        var svc = Build(brandRatio: 0.25m);

        // Cost = 40000 × 1.25 = 50000; Retail = ceil(50000/0.25 = 200000) → 200000;
        // Wholesale = floor(200000 − (200000−50000)/2 = 125000) → 125000.
        var r = await svc.CalculateAsync(40000m, "VIKA", CancellationToken.None);

        Assert.Equal(50000m, r.Cost);
        Assert.Equal(200000m, r.Retail);
        Assert.Equal(125000m, r.Wholesale);
        Assert.Equal(0.25m, r.RatioUsed);
    }

    [Fact]
    public async Task Calculate_RetailCeilsToMagnitudeIncrement()
    {
        var svc = Build(brandRatio: 0.27m);

        // Cost = 50000; RetailRaw = 50000/0.27 = 185185.19 → ceil to nearest 5000 = 190000.
        // WholesaleRaw = 190000 − (190000−50000)/2 = 120000 → floor to 5000 = 120000.
        var r = await svc.CalculateAsync(40000m, "DPA", CancellationToken.None);

        Assert.Equal(50000m, r.Cost);
        Assert.Equal(190000m, r.Retail);
        Assert.Equal(120000m, r.Wholesale);
    }

    [Fact]
    public async Task Calculate_FallsBackToDefaultBrand_WhenBrandHasNoBand()
    {
        var svc = Build(brandRatio: null, defaultRatio: 0.20m);

        // Unknown brand → DEFAULT ratio 0.20. Cost = 5000; Retail = ceil(25000) → 25000 (nearest 1000);
        // Wholesale = floor(25000 − 10000 = 15000) → 15000.
        var r = await svc.CalculateAsync(4000m, "SomeUnknownBrand", CancellationToken.None);

        Assert.Equal(5000m, r.Cost);
        Assert.Equal(25000m, r.Retail);
        Assert.Equal(15000m, r.Wholesale);
        Assert.Equal(0.20m, r.RatioUsed);
    }

    [Fact]
    public async Task Calculate_NudgesWholesaleAboveCost_WhenMidpointRoundsToCost()
    {
        var svc = Build(brandRatio: 0.95m);

        // Cost = 10000; RetailRaw = 10526.32 → ceil to 1000 = 11000;
        // WholesaleRaw = 11000 − 500 = 10500 → floor to 1000 = 10000, which is NOT > Cost,
        // so it falls back to Cost + WholesaleFloorOverCost(1000) = 11000.
        var r = await svc.CalculateAsync(8000m, "OE", CancellationToken.None);

        Assert.Equal(10000m, r.Cost);
        Assert.Equal(11000m, r.Retail);
        Assert.Equal(11000m, r.Wholesale);
        Assert.True(r.Wholesale > r.Cost);
    }

    [Fact]
    public async Task Calculate_Throws_OnNonPositivePrice()
    {
        var svc = Build(brandRatio: 0.25m);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.CalculateAsync(0m, "VIKA", CancellationToken.None));
    }
}
