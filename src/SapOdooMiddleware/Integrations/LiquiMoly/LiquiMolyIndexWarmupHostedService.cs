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

        await WarmSafelyAsync(stoppingToken);

        var interval = TimeSpan.FromHours(Math.Max(1, _settings.WarmupIntervalHours));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await WarmSafelyAsync(stoppingToken);
    }

    private async Task WarmSafelyAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[LiquiMoly] Warming product index in the background...");
            using var scope = _scopeFactory.CreateScope();
            var scraper = scope.ServiceProvider.GetRequiredService<LiquiMolyProductScraperService>();
            await scraper.WarmIndexAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LiquiMoly] Index warmup failed; will retry on the next tick.");
        }
    }
}
