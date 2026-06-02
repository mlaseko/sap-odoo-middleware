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
        public Task<decimal?> GetRatioAsync(string brand, decimal pl01Tzs, CancellationToken ct) => Task.FromResult(_ratio);
    }

    private static PricingCalculationService Build(decimal? ratio, decimal markup = 1.25m, decimal fallback = 0.85m)
        => new(new FakeRatioRepo(ratio),
               Options.Create(new AutohubPricingSettings { RetailMarkup = markup, DefaultPl01ToPl03 = fallback }));

    [Fact]
    public async Task Calculate_AppliesMarkupRatioAndMidpoint()
    {
        var svc = Build(ratio: 0.85m);

        var r = await svc.CalculateAsync(70200m, "LR", CancellationToken.None);

        Assert.Equal(87750.00m, r.Pl01);                  // 70200 × 1.25
        Assert.Equal(74587.50m, r.Pl03);                  // 87750 × 0.85
        Assert.Equal(81168.75m, r.Pl05);                  // (87750 + 74587.50) / 2
        Assert.Equal(0.85m, r.RatioUsed);
    }

    [Fact]
    public async Task Calculate_FallsBackToDefaultRatio_WhenNoBandMatches()
    {
        var svc = Build(ratio: null, fallback: 0.80m);

        var r = await svc.CalculateAsync(100000m, "LR", CancellationToken.None);

        Assert.Equal(125000.00m, r.Pl01);
        Assert.Equal(100000.00m, r.Pl03);                 // 125000 × 0.80
        Assert.Equal(0.80m, r.RatioUsed);
    }

    [Fact]
    public async Task Calculate_RoundsToTwoDecimals()
    {
        var svc = Build(ratio: 0.83m);
        var r = await svc.CalculateAsync(33333.33m, "MB", CancellationToken.None);

        // PL01 = 41666.6625 -> 41666.66; PL03 = 41666.66 × 0.83 = 34583.3278 -> 34583.33
        Assert.Equal(41666.66m, r.Pl01);
        Assert.Equal(34583.33m, r.Pl03);
    }
}
