using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Workers;

/// <summary>
/// Polls Neon for items whose Odoo product has been created (OdooProductId set) but
/// whose SAP <c>U_Odoo_Product_ID</c> UDF has not yet been stamped, and back-stamps
/// the Odoo id onto SAP. Marks each item with BackrefStampedAt so it isn't reprocessed.
/// </summary>
public class OdooBackrefWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly OdooBackrefWorkerSettings _settings;
    private readonly ILogger<OdooBackrefWorker> _logger;

    public OdooBackrefWorker(
        IServiceProvider sp,
        IOptions<OdooBackrefWorkerSettings> settings,
        ILogger<OdooBackrefWorker> logger)
    {
        _sp       = sp;
        _settings = settings.Value;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("OdooBackrefWorker is disabled by configuration.");
            return;
        }
        var interval = TimeSpan.FromSeconds(Math.Max(30, _settings.PollIntervalSeconds));
        _logger.LogInformation("OdooBackrefWorker starting; interval = {Interval}s", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "OdooBackrefWorker cycle failed."); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task ProcessOnceAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var productRepo = scope.ServiceProvider.GetRequiredService<INeonProductRepository>();
        var sap         = scope.ServiceProvider.GetRequiredService<ISapB1Service>();

        var pending = await productRepo.GetItemsAwaitingBackrefAsync(50, ct);
        if (pending.Count == 0) return;

        _logger.LogInformation("Backref: {Count} items awaiting Odoo id stamp.", pending.Count);

        foreach (var p in pending)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(p.OdooProductId)) continue;
            try
            {
                await sap.UpdateOdooProductIdAsync(p.ItemCode, p.OdooProductId);
                await productRepo.MarkBackrefStampedAsync(p.ItemCode, ct);
                _logger.LogInformation("Backref stamped: SAP {Code} ← Odoo {Id}.", p.ItemCode, p.OdooProductId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backref failed for SAP {Code}.", p.ItemCode);
            }
        }
    }
}
