using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Pricing;

/// <summary>
/// Loads the saved ratio overrides from Neon at startup and applies them to the
/// (singleton) <see cref="PricingCalculator"/>, so UI-approved ratios survive
/// restarts. Failure to load is logged but never blocks startup — the calculator
/// then runs on the hard-coded defaults until the next save.
/// </summary>
public sealed class PricingRatioLoaderHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPricingCalculator _calc;
    private readonly ILogger<PricingRatioLoaderHostedService> _logger;

    public PricingRatioLoaderHostedService(
        IServiceScopeFactory scopeFactory,
        IPricingCalculator calc,
        ILogger<PricingRatioLoaderHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _calc = calc;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ILubesPricingRepository>();
            var (band, maasai) = await repo.GetRatioOverridesAsync(cancellationToken);
            if (band.Count > 0 || maasai.Count > 0)
            {
                _calc.ApplyRatioOverrides(band, maasai);
                _logger.LogInformation(
                    "Pricing ratio overrides loaded: {Band} band + {Maasai} Maasai override(s) applied.",
                    band.Count, maasai.Count);
            }
            else
            {
                _logger.LogInformation("No pricing ratio overrides stored — calculator runs on defaults.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to load pricing ratio overrides — calculator runs on defaults until the next save.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
