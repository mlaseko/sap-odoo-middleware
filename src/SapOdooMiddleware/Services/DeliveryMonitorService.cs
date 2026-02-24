using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Odoo;

namespace SapOdooMiddleware.Services;

/// <summary>
/// Sends delivery webhook status notifications to the Odoo Integration
/// Control Center via its JSON controller endpoint.
/// </summary>
public class DeliveryMonitorService : IDeliveryMonitorService
{
    private readonly IOptionsMonitor<MonitorSettings> _settingsMonitor;
    private readonly HttpClient _httpClient;
    private readonly ILogger<DeliveryMonitorService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public DeliveryMonitorService(
        IOptionsMonitor<MonitorSettings> settingsMonitor,
        HttpClient httpClient,
        ILogger<DeliveryMonitorService> logger)
    {
        _settingsMonitor = settingsMonitor;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task NotifyAsync(DeliveryMonitorPayload payload)
    {
        var settings = _settingsMonitor.CurrentValue;

        if (!settings.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(settings.CallbackUrl))
        {
            _logger.LogDebug("DeliveryMonitor: CallbackUrl not configured, skipping notification.");
            return;
        }

        // Inject the API key from configuration
        payload.ApiKey = settings.ApiKey;

        try
        {
            // Odoo type='json' controllers expect {"jsonrpc":"2.0","method":"call","params":{...}}
            var rpcPayload = new
            {
                jsonrpc = "2.0",
                method = "call",
                id = 1,
                @params = payload,
            };

            var json = JsonSerializer.Serialize(rpcPayload, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(settings.CallbackUrl, content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "DeliveryMonitor: Notified Odoo for SO={OdooSoId}, SAP={SapDeliveryNo}, State={State}",
                    payload.OdooSoId, payload.SapDeliveryNo, payload.State);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "DeliveryMonitor: Odoo callback returned {StatusCode}: {Body}",
                    (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            // Log but do not throw — monitoring should never break the main flow
            _logger.LogWarning(ex,
                "DeliveryMonitor: Failed to notify Odoo for SO={OdooSoId}, SAP={SapDeliveryNo}",
                payload.OdooSoId, payload.SapDeliveryNo);
        }
    }
}
