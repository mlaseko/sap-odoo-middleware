using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

public class PricingCalculationServiceTests
{
    private sealed class FakeRatioRepo : IPricingBrandRatioRepository
    {
        private readonly decimal? _ratio;
        public FakeRatioRepo(decimal? ratio) => _ratio = ratio;
        public Task<decimal?> GetRatioAsync(string brand, decimal retailTzs, CancellationToken ct) => Task.FromResult(_ratio);
    }

    private static PricingCalculationService Build(decimal? ratio, decimal markup = 1.25m, decimal fallback = 0.85m)
        => new(new FakeRatioRepo(ratio),
               Options.Create(new AutohubPricingSettings { RetailMarkup = markup, DefaultWholesaleRatio = fallback }));

    [Fact]
    public async Task Calculate_MapsCostRetailWholesale()
    {
        var svc = Build(ratio: 0.85m);

        var r = await svc.CalculateAsync(70200m, "LR", CancellationToken.None);

        Assert.Equal(70200.00m, r.Cost);          // PL01 = landed cost, as-is
        Assert.Equal(87750.00m, r.Retail);        // PL03 = 70200 × 1.25
        Assert.Equal(74587.50m, r.Wholesale);     // PL05 = 87750 × 0.85
        Assert.Equal(0.85m, r.RatioUsed);
    }

    [Fact]
    public async Task Calculate_FallsBackToDefaultRatio_WhenNoBandMatches()
    {
        var svc = Build(ratio: null, fallback: 0.80m);

        var r = await svc.CalculateAsync(100000m, "LR", CancellationToken.None);

        Assert.Equal(100000.00m, r.Cost);
        Assert.Equal(125000.00m, r.Retail);       // 100000 × 1.25
        Assert.Equal(100000.00m, r.Wholesale);    // 125000 × 0.80
        Assert.Equal(0.80m, r.RatioUsed);
    }

    [Fact]
    public async Task Calculate_RoundsToTwoDecimals()
    {
        var svc = Build(ratio: 0.83m);
        var r = await svc.CalculateAsync(33333.33m, "MB", CancellationToken.None);

        Assert.Equal(33333.33m, r.Cost);
        Assert.Equal(41666.66m, r.Retail);        // 33333.33 × 1.25 = 41666.6625 -> 41666.66
        Assert.Equal(34583.33m, r.Wholesale);     // 41666.66 × 0.83 = 34583.3278 -> 34583.33
    }
}
