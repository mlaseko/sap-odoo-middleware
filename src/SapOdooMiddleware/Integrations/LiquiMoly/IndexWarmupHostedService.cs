using Microsoft.Extensions.Options;

namespace MolasLubes.Infrastructure.Integrations.LiquiMoly;

/// <summary>
/// Builds a brand's product index off the request path: once shortly after startup (reusing a fresh
/// persisted index if present) and then on a timer just under the cache lifetime. Without this the first
/// /scrape or bulk-create pays the full cold crawl + variant mining, which exceeds the CDN's ~100s request
/// timeout. Generic over the brand scraper and its settings so Liqui Moly and Meguin share identical
/// warm-up / retry / persist behaviour.
/// </summary>
public sealed class IndexWarmupHostedService<TScraper, TSettings> : BackgroundService
    where TScraper : LiquiMolyProductScraperService
    where TSettings : LiquiMolyScraperSettings
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TSettings _settings;
    private readonly ILogger<IndexWarmupHostedService<TScraper, TSettings>> _logger;

    // "LiquiMolyProductScraperService" -> "LiquiMoly", "MeguinProductScraperService" -> "Meguin".
    private static readonly string Brand = typeof(TScraper).Name.Replace("ProductScraperService", "");

    public IndexWarmupHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<TSettings> settings,
        ILogger<IndexWarmupHostedService<TScraper, TSettings>> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.WarmupOnStartup)
        {
            _logger.LogInformation("[{Brand}] Index warmup disabled (WarmupOnStartup=false).", Brand);
            return;
        }

        // Let the rest of the app finish starting before kicking off a crawl.
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        // Startup: reuse a persisted index if it's still fresh (instant) instead of cold-crawling. Retry on
        // a short interval until the index is actually warm — a throttled first build can come back partial
        // and is intentionally not cached, so one attempt isn't always enough.
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await WarmSafelyAsync(forceRebuild: false, stoppingToken))
                break;

            var retry = TimeSpan.FromMinutes(Math.Max(1, _settings.WarmupRetryMinutes));
            _logger.LogWarning("[{Brand}] Index not warm yet; retrying in {Min} min.", Brand, retry.TotalMinutes);
            try { await Task.Delay(retry, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _settings.WarmupIntervalHours));
        using var timer = new PeriodicTimer(interval);
        // Timer: refresh from the live site and re-persist so the cache never goes stale.
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await WarmSafelyAsync(forceRebuild: true, stoppingToken);
    }

    /// <summary>Runs one warm/rebuild pass. Returns true if the index is warm afterwards.</summary>
    private async Task<bool> WarmSafelyAsync(bool forceRebuild, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[{Brand}] {Mode} product index in the background...",
                Brand, forceRebuild ? "Rebuilding" : "Warming");
            using var scope = _scopeFactory.CreateScope();
            var scraper = scope.ServiceProvider.GetRequiredService<TScraper>();
            await scraper.WarmIndexAsync(forceRebuild, ct);
            return scraper.IsIndexWarm();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false; // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Brand}] Index warmup failed; will retry.", Brand);
            return false;
        }
    }
}
