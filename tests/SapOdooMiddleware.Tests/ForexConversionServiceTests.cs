using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

public class ForexConversionServiceTests
{
    private sealed class FakeForexRepo : IForexRateRepository
    {
        private readonly decimal? _rate;
        public int Calls { get; private set; }
        public FakeForexRepo(decimal? rate) => _rate = rate;
        public Task<decimal?> GetRateAsync(string currency, DateTime asOf, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_rate);
        }
    }

    private static ForexConversionService Build(FakeForexRepo repo)
        => new(repo, new MemoryCache(new MemoryCacheOptions()),
               Options.Create(new AutohubPricingSettings { ForexCacheMinutes = 5 }));

    private static readonly DateTime AsOf = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ConvertToTzs_MultipliesByRate()
    {
        var svc = Build(new FakeForexRepo(2700m));
        var tzs = await svc.ConvertToTzsAsync(26m, "USD", AsOf, CancellationToken.None);
        Assert.Equal(70200m, tzs);
    }

    [Fact]
    public async Task GetRate_CachesAcrossCalls()
    {
        var repo = new FakeForexRepo(2700m);
        var svc = Build(repo);

        await svc.GetRateAsync("USD", AsOf, CancellationToken.None);
        await svc.GetRateAsync("USD", AsOf, CancellationToken.None);
        await svc.GetRateAsync("USD", AsOf, CancellationToken.None);

        Assert.Equal(1, repo.Calls);   // only the first call hits the repository
    }

    [Fact]
    public async Task GetRate_Throws_WhenNoRateConfigured()
    {
        var svc = Build(new FakeForexRepo(null));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetRateAsync("AED", AsOf, CancellationToken.None));
    }
}
