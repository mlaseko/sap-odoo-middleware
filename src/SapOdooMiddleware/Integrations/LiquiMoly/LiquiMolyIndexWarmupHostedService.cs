using Microsoft.Extensions.Options;

namespace MolasLubes.Infrastructure.Integrations.LiquiMoly;

/// <summary>
/// Builds the Liqui Moly product index off the request path: once shortly after startup and then on a
/// timer (just under the cache lifetime). Without this, the first /scrape or bulk-create call pays the
/// full cold crawl + variant-mining cost, which exceeds the CDN's ~100s request timeout and returns 524.
/// </summary>
public sealed class LiquiMolyIndexWarmupHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LiquiMolyScraperSettings _settings;
    private readonly ILogger<LiquiMolyIndexWarmupHostedService> _logger;

    public LiquiMolyIndexWarmupHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<LiquiMolyScraperSettings> settings,
        ILogger<LiquiMolyIndexWarmupHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.WarmupOnStartup)
        {
            _logger.LogInformation("[LiquiMoly] Index warmup disabled (LiquiMoly:WarmupOnStartup=false).");
            return;
        }

        // Let the rest of the app finish starting before kicking off a heavy crawl.
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
            _logger.LogWarning("[LiquiMoly] Index not warm yet; retrying in {Min} min.", retry.TotalMinutes);
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
            _logger.LogInformation("[LiquiMoly] {Mode} product index in the background...",
                forceRebuild ? "Rebuilding" : "Warming");
            using var scope = _scopeFactory.CreateScope();
            var scraper = scope.ServiceProvider.GetRequiredService<LiquiMolyProductScraperService>();
            await scraper.WarmIndexAsync(forceRebuild, ct);
            return scraper.IsIndexWarm();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false; // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LiquiMoly] Index warmup failed; will retry.");
            return false;
        }
    }
}
