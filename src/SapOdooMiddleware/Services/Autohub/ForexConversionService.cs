using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

public interface IForexConversionService
{
    Task<decimal> ConvertToTzsAsync(decimal amount, string fromCurrency, DateTime asOf, CancellationToken ct);
    Task<decimal> GetRateAsync(string currency, DateTime asOf, CancellationToken ct);
}

/// <summary>
/// Converts supplier-currency amounts to TZS using the manually-maintained forex_rate table.
/// Rates change infrequently, so they're cached in <see cref="IMemoryCache"/> (cross-request,
/// keyed by currency + effective date) for AutohubPricing:ForexCacheMinutes. Throws if no rate
/// row exists for the requested currency/date — pricing must never silently use a wrong rate.
/// </summary>
public sealed class ForexConversionService : IForexConversionService
{
    private readonly IForexRateRepository _repo;
    private readonly IMemoryCache _cache;
    private readonly AutohubPricingSettings _settings;

    public ForexConversionService(
        IForexRateRepository repo, IMemoryCache cache, IOptions<AutohubPricingSettings> settings)
    {
        _repo = repo;
        _cache = cache;
        _settings = settings.Value;
    }

    public async Task<decimal> GetRateAsync(string currency, DateTime asOf, CancellationToken ct)
    {
        var key = $"forex:{currency.ToUpperInvariant()}:{asOf:yyyy-MM-dd}";
        if (_cache.TryGetValue(key, out decimal cached))
            return cached;

        var rate = await _repo.GetRateAsync(currency, asOf, ct)
            ?? throw new InvalidOperationException(
                $"No forex rate configured for {currency} as of {asOf:yyyy-MM-dd}. Add it to forex_rate before creating items.");

        _cache.Set(key, rate, TimeSpan.FromMinutes(_settings.ForexCacheMinutes));
        return rate;
    }

    public async Task<decimal> ConvertToTzsAsync(decimal amount, string fromCurrency, DateTime asOf, CancellationToken ct)
    {
        var rate = await GetRateAsync(fromCurrency, asOf, ct);
        return amount * rate;
    }
}
