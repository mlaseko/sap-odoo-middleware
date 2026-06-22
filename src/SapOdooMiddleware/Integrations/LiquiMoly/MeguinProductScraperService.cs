using Microsoft.Extensions.Options;

namespace MolasLubes.Infrastructure.Integrations.LiquiMoly;

/// <summary>
/// Meguin scraper — Meguin is a Liqui Moly subsidiary on the same Magento platform, so this reuses the
/// entire <see cref="LiquiMolyProductScraperService"/> pipeline (index build, variant mining, jsonConfig
/// size parsing, persistence) pointed at meguin.com via <see cref="MeguinScraperSettings"/>. Overriding
/// <see cref="BrandKey"/> gives it its own in-memory cache slot, persisted index file and log prefix,
/// fully isolated from the Liqui Moly index.
/// </summary>
public sealed class MeguinProductScraperService : LiquiMolyProductScraperService
{
    public MeguinProductScraperService(
        HttpClient httpClient,
        IOptions<MeguinScraperSettings> settings,
        ILogger<MeguinProductScraperService> logger)
        : base(httpClient, settings.Value, logger) { }

    protected override string BrandKey  => "Meguin";
    protected override string LogPrefix => "[Meguin] ";
}
