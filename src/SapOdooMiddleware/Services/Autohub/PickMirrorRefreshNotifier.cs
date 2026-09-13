using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Fire-and-forget nudge to the zone-fulfillment sync service after a pick-list
/// DI API write commits, so the SQLite/Neon mirror refreshes that AbsEntry in
/// seconds instead of waiting for the service's 30-second reconciliation poll.
/// Disabled until both <see cref="AutohubInventorySettings.PickMirrorHookBaseUrl"/>
/// and <see cref="AutohubInventorySettings.PickMirrorHookApiKey"/> are configured.
/// Never blocks or fails the pick response: errors are logged at warning and the
/// sync service's own poll remains the safety net.
/// </summary>
public interface IPickMirrorRefreshNotifier
{
    /// <summary>Requests a targeted mirror refresh for one pick list (OPKL AbsEntry).</summary>
    void NotifyPickListChanged(int absEntry);
}

public sealed class PickMirrorRefreshNotifier : IPickMirrorRefreshNotifier
{
    private readonly HttpClient _http;
    private readonly AutohubInventorySettings _settings;
    private readonly ILogger<PickMirrorRefreshNotifier> _logger;

    public PickMirrorRefreshNotifier(
        HttpClient http,
        IOptions<AutohubInventorySettings> settings,
        ILogger<PickMirrorRefreshNotifier> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
    }

    public void NotifyPickListChanged(int absEntry)
    {
        var baseUrl = _settings.PickMirrorHookBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(_settings.PickMirrorHookApiKey))
            return;

        // Deliberately not awaited by the caller: the SAP write has already
        // committed, and the hook must never delay or fail the pick response.
        _ = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, $"{baseUrl}/internal/refresh/pick-list/{absEntry}");
                request.Headers.TryAddWithoutValidation("X-API-Key", _settings.PickMirrorHookApiKey);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var response = await _http.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Pick mirror refresh hook answered {StatusCode} for pick list {AbsEntry}; " +
                        "the sync service's reconciliation poll will catch up.",
                        (int)response.StatusCode, absEntry);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Pick mirror refresh hook failed for pick list {AbsEntry}; " +
                    "the sync service's reconciliation poll will catch up.", absEntry);
            }
        });
    }
}
