using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Drives <see cref="ISapSkuCounterRefreshService"/> on a schedule: once at startup (if enabled)
/// and then on a daily timer. On-demand refreshes go through the admin endpoint, not this service.
/// </summary>
public sealed class SkuCounterRefreshHostedService : BackgroundService
{
    private readonly ISapSkuCounterRefreshService _refresh;
    private readonly AutohubSkuRefreshSettings _settings;
    private readonly ILogger<SkuCounterRefreshHostedService> _logger;

    public SkuCounterRefreshHostedService(
        ISapSkuCounterRefreshService refresh,
        IOptions<AutohubSkuRefreshSettings> settings,
        ILogger<SkuCounterRefreshHostedService> logger)
    {
        _refresh = refresh;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("SKU counter refresh is disabled (AutohubSkuRefresh:Enabled=false).");
            return;
        }

        if (_settings.RefreshOnStartup)
            await RunSafelyAsync(stoppingToken);

        var interval = TimeSpan.FromHours(Math.Max(1, _settings.IntervalHours));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunSafelyAsync(stoppingToken);
    }

    private async Task RunSafelyAsync(CancellationToken ct)
    {
        try
        {
            var results = await _refresh.RefreshAllAsync(ct);
            var bumped = results.Count(r => r.NeonNew > r.NeonWas);
            _logger.LogInformation("SKU counter refresh complete: {Total} prefix(es), {Bumped} bumped.", results.Count, bumped);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SKU counter refresh failed; will retry on the next tick.");
        }
    }
}
