using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Api;

namespace SapOdooMiddleware.Services.Odoo;

public class OdooJsonRpcClient : IOdooJsonRpcClient
{
    private readonly HttpClient _httpClient;
    private readonly OdooSettings _settings;
    private readonly ILogger<OdooJsonRpcClient> _logger;
    private int? _uid;

    public OdooJsonRpcClient(HttpClient httpClient, IOptions<OdooSettings> settings, ILogger<OdooJsonRpcClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
        _httpClient.BaseAddress = new Uri(_settings.Url.TrimEnd('/') + "/");
    }

    public async Task<DeliveryConfirmationResponse> ConfirmDeliveryAsync(DeliveryConfirmationRequest request)
    {
        await EnsureAuthenticatedAsync();

        _logger.LogInformation(
            "Processing delivery confirmation for Odoo SO: {OdooSoRef}, SAP Delivery: {SapDeliveryNo}",
            request.OdooSoRef, request.SapDeliveryNo);

        // Step 1: Find sale.order by odoo_so_ref
        var soIds = await SearchReadAsync("sale.order",
            new object[] { new object[] { "name", "=", request.OdooSoRef } },
            new[] { "id", "name", "picking_ids" });

        if (soIds.Count == 0)
        {
            throw new InvalidOperationException($"Sale order not found in Odoo for ref: {request.OdooSoRef}");
        }

        var saleOrder = soIds[0];
        var soId = saleOrder.GetProperty("id").GetInt32();
        var soName = saleOrder.GetProperty("name").GetString();

        _logger.LogInformation("Found Odoo sale.order: id={SoId}, name={SoName}", soId, soName);

        // Step 2: Get related outgoing stock.picking that is not done/cancelled
        var pickingIds = await SearchReadAsync("stock.picking",
            new object[]
            {
                new object[] { "sale_id", "=", soId },
                new object[] { "picking_type_code", "=", "outgoing" },
                new object[] { "state", "not in", new[] { "done", "cancel" } }
            },
            new[] { "id", "name", "state" });

        if (pickingIds.Count == 0)
        {
            _logger.LogWarning("No pending outgoing picking found for SO {SoName}", soName);
            return new DeliveryConfirmationResponse
            {
                OdooSoRef = request.OdooSoRef,
                SapDeliveryNo = request.SapDeliveryNo,
                Status = "no_pending_picking",
                Message = $"No pending outgoing delivery found for SO {request.OdooSoRef}"
            };
        }

        var picking = pickingIds[0];
        var pickingId = picking.GetProperty("id").GetInt32();
        var pickingName = picking.GetProperty("name").GetString() ?? string.Empty;

        _logger.LogInformation("Found pending picking: id={PickingId}, name={PickingName}, state={State}",
            pickingId, pickingName, picking.GetProperty("state").GetString());

        // Step 3: Run Odoo standard workflow programmatically
        // action_assign() - reserve stock
        await CallMethodAsync("stock.picking", "action_assign", new object[] { new[] { pickingId } });
        _logger.LogInformation("action_assign completed for picking {PickingName}", pickingName);

        // action_set_quantities_to_reservation() - set qty_done = reserved
        await CallMethodAsync("stock.picking", "action_set_quantities_to_reservation",
            new object[] { new[] { pickingId } });
        _logger.LogInformation("action_set_quantities_to_reservation completed for picking {PickingName}", pickingName);

        // button_validate() with context to skip wizards
        await CallMethodWithContextAsync("stock.picking", "button_validate",
            new object[] { new[] { pickingId } },
            new Dictionary<string, object>
            {
                { "skip_backorder", true },
                { "skip_immediate", true },
                { "picking_ids_not_to_backorder", new[] { pickingId } }
            });
        _logger.LogInformation("button_validate completed for picking {PickingName}", pickingName);

        // Step 4: Write SAP delivery number/date onto the picking and SO
        await WriteAsync("stock.picking", pickingId, new Dictionary<string, object>
        {
            { "x_sap_delivery_no", request.SapDeliveryNo },
            { "x_sap_delivery_date", request.DeliveryDate }
        });

        await WriteAsync("sale.order", soId, new Dictionary<string, object>
        {
            { "x_sap_delivery_no", request.SapDeliveryNo },
            { "x_sap_delivery_date", request.DeliveryDate }
        });

        _logger.LogInformation(
            "Delivery confirmation complete: Odoo SO={OdooSoRef}, Picking={PickingName}, SAP Delivery={SapDeliveryNo}",
            request.OdooSoRef, pickingName, request.SapDeliveryNo);

        return new DeliveryConfirmationResponse
        {
            OdooSoRef = request.OdooSoRef,
            SapDeliveryNo = request.SapDeliveryNo,
            OdooPickingId = pickingId,
            OdooPickingName = pickingName,
            Status = "done",
            Message = $"Delivery confirmed: picking {pickingName} validated and marked as done"
        };
    }

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            await EnsureAuthenticatedAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Odoo health check failed");
            return false;
        }
    }

    private async Task EnsureAuthenticatedAsync()
    {
        if (_uid != null) return;

        var payload = CreateJsonRpcPayload("common", "authenticate",
            new object[] { _settings.Database, _settings.User, _settings.Password, new { } });

        var response = await _httpClient.PostAsJsonAsync("jsonrpc", payload);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var resultValue = result.GetProperty("result");

        if (resultValue.ValueKind == JsonValueKind.False || resultValue.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidOperationException("Odoo authentication failed");
        }

        _uid = resultValue.GetInt32();
        _logger.LogInformation("Authenticated with Odoo as uid={Uid}", _uid);
    }

    private async Task<List<JsonElement>> SearchReadAsync(string model, object[] domain, string[] fields)
    {
        var payload = CreateJsonRpcPayload("object", "execute_kw",
            new object[]
            {
                _settings.Database, _uid!, _settings.Password,
                model, "search_read",
                new object[] { domain },
                new { fields }
            });

        var response = await _httpClient.PostAsJsonAsync("jsonrpc", payload);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        CheckForError(result);

        var records = result.GetProperty("result");
        var list = new List<JsonElement>();
        foreach (var record in records.EnumerateArray())
        {
            list.Add(record);
        }
        return list;
    }

    private async Task CallMethodAsync(string model, string method, object[] args)
    {
        var payload = CreateJsonRpcPayload("object", "execute_kw",
            new object[]
            {
                _settings.Database, _uid!, _settings.Password,
                model, method,
                args
            });

        var response = await _httpClient.PostAsJsonAsync("jsonrpc", payload);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        CheckForError(result);
    }

    private async Task CallMethodWithContextAsync(string model, string method, object[] args,
        Dictionary<string, object> context)
    {
        var payload = CreateJsonRpcPayload("object", "execute_kw",
            new object[]
            {
                _settings.Database, _uid!, _settings.Password,
                model, method,
                args,
                new { context }
            });

        var response = await _httpClient.PostAsJsonAsync("jsonrpc", payload);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        CheckForError(result);
    }

    private async Task WriteAsync(string model, int id, Dictionary<string, object> values)
    {
        var payload = CreateJsonRpcPayload("object", "execute_kw",
            new object[]
            {
                _settings.Database, _uid!, _settings.Password,
                model, "write",
                new object[] { new[] { id }, values }
            });

        var response = await _httpClient.PostAsJsonAsync("jsonrpc", payload);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        CheckForError(result);
    }

    private static object CreateJsonRpcPayload(string service, string method, object[] args)
    {
        return new
        {
            jsonrpc = "2.0",
            method = "call",
            @params = new
            {
                service,
                method,
                args
            }
        };
    }

    private static void CheckForError(JsonElement result)
    {
        if (result.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("data", out var data) && data.TryGetProperty("message", out var msg)
                ? msg.GetString()
                : error.GetProperty("message").GetString();
            throw new InvalidOperationException($"Odoo JSON-RPC error: {message}");
        }
    }
}
