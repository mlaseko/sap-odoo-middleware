using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Odoo;

namespace SapOdooMiddleware.Services;

/// <summary>
/// Odoo JSON-RPC client that confirms deliveries by programmatically running
/// the standard stock.picking workflow: reserve → set quantities → validate.
/// </summary>
public class OdooJsonRpcService : IOdooService
{
    private readonly OdooSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OdooJsonRpcService> _logger;

    private int? _uid;
    private int _rpcId;

    public OdooJsonRpcService(
        IOptions<OdooSettings> settings,
        HttpClient httpClient,
        ILogger<OdooJsonRpcService> logger)
    {
        _settings = settings.Value;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<DeliveryUpdateResponse> ConfirmDeliveryAsync(DeliveryUpdateRequest request)
    {
        await EnsureAuthenticatedAsync();

        var soId = request.ResolvedSoId;

        // 1. Find sale.order by name (which matches the Odoo SO identifier, e.g. "SO0042")
        var soIds = await SearchAsync("sale.order", new JsonArray
        {
            new JsonArray { JsonValue.Create("name"), JsonValue.Create("="), JsonValue.Create(soId) }
        });

        if (soIds.Count == 0)
            throw new InvalidOperationException($"Sale order '{soId}' not found in Odoo.");

        int soDbId = soIds[0];
        _logger.LogInformation("Found Odoo sale.order id={SoId} for ref={UOdooSoId}", soDbId, soId);

        // 2. Find related outgoing stock.picking that is not done/cancelled
        var pickingIds = await SearchAsync("stock.picking", new JsonArray
        {
            new JsonArray { JsonValue.Create("sale_id"), JsonValue.Create("="), JsonValue.Create(soDbId) },
            new JsonArray { JsonValue.Create("picking_type_code"), JsonValue.Create("="), JsonValue.Create("outgoing") },
            new JsonArray { JsonValue.Create("state"), JsonValue.Create("not in"), new JsonArray { JsonValue.Create("done"), JsonValue.Create("cancel") } }
        });

        if (pickingIds.Count == 0)
            throw new InvalidOperationException(
                $"No pending outgoing picking found for sale order '{soId}'.");

        int pickingId = pickingIds[0];
        _logger.LogInformation("Found Odoo stock.picking id={PickingId} for SO ref={UOdooSoId}", pickingId, soId);

        // 3. action_assign() — reserve stock
        await ExecuteMethodAsync("stock.picking", "action_assign", new JsonArray { pickingId });
        _logger.LogInformation("Reserved stock for picking id={PickingId}", pickingId);

        // 4. action_set_quantities_to_reservation() — set qty_done = demand/reserved
        await ExecuteMethodAsync("stock.picking", "action_set_quantities_to_reservation", new JsonArray { pickingId });
        _logger.LogInformation("Set quantities to reservation for picking id={PickingId}", pickingId);

        // 5. button_validate() — with context to skip backorder/immediate-transfer wizards
        await ExecuteMethodWithContextAsync("stock.picking", "button_validate", new JsonArray { pickingId },
            new JsonObject
            {
                ["skip_backorder"] = true,
                ["skip_immediate"] = true
            });
        _logger.LogInformation("Validated picking id={PickingId}", pickingId);

        // 6. Write SAP delivery number and date onto the picking
        var writeValues = new JsonObject
        {
            ["x_sap_delivery_no"] = request.SapDeliveryNo
        };
        if (request.DeliveryDate.HasValue)
            writeValues["x_sap_delivery_date"] = request.DeliveryDate.Value.ToString("yyyy-MM-dd");

        await WriteAsync("stock.picking", pickingId, writeValues);
        _logger.LogInformation("Wrote SAP delivery ref onto picking id={PickingId}", pickingId);

        // 7. Read back picking state and name
        var pickingData = await ReadAsync("stock.picking", pickingId, new JsonArray
        {
            JsonValue.Create("name"), JsonValue.Create("state")
        });

        string pickingName = pickingData?["name"]?.GetValue<string>() ?? "";
        string state = pickingData?["state"]?.GetValue<string>() ?? "";

        return new DeliveryUpdateResponse
        {
            UOdooSoId = soId,
            PickingId = pickingId,
            PickingName = pickingName,
            State = state,
            SapDeliveryNo = request.SapDeliveryNo
        };
    }

    // ── Odoo JSON-RPC helpers ────────────────────────────────────────

    private async Task EnsureAuthenticatedAsync()
    {
        if (_uid.HasValue) return;

        var result = await CallJsonRpcAsync("/web/session/authenticate", new JsonObject
        {
            ["db"] = _settings.Database,
            ["login"] = _settings.UserName,
            ["password"] = _settings.Password
        });

        _uid = result?["uid"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Odoo authentication failed — uid is null.");

        _logger.LogInformation("Authenticated with Odoo as uid={Uid}", _uid);
    }

    private async Task<List<int>> SearchAsync(string model, JsonArray domain)
    {
        var result = await CallObjectMethodAsync(model, "search", new JsonArray { domain });
        return result?.AsArray().Select(n => n!.GetValue<int>()).ToList() ?? [];
    }

    private async Task ExecuteMethodAsync(string model, string method, JsonArray ids)
    {
        await CallObjectMethodAsync(model, method, new JsonArray { ids });
    }

    private async Task ExecuteMethodWithContextAsync(string model, string method, JsonArray ids, JsonObject context)
    {
        var kwargs = new JsonObject { ["context"] = context };
        await CallObjectMethodAsync(model, method, new JsonArray { ids }, kwargs);
    }

    private async Task WriteAsync(string model, int id, JsonObject values)
    {
        await CallObjectMethodAsync(model, "write", new JsonArray
        {
            new JsonArray { id },
            values
        });
    }

    private async Task<JsonObject?> ReadAsync(string model, int id, JsonArray fields)
    {
        var result = await CallObjectMethodAsync(model, "read", new JsonArray
        {
            new JsonArray { id },
            fields
        });

        return result?.AsArray().FirstOrDefault()?.AsObject();
    }

    private async Task<JsonNode?> CallObjectMethodAsync(
        string model, string method, JsonArray args, JsonObject? kwargs = null)
    {
        var callArgs = new JsonArray
        {
            JsonValue.Create(_settings.Database),
            JsonValue.Create(_uid!.Value),
            JsonValue.Create(_settings.Password),
            JsonValue.Create(model),
            JsonValue.Create(method)
        };

        // Flatten args into callArgs
        foreach (var arg in args)
        {
            callArgs.Add(arg?.DeepClone());
        }

        if (kwargs != null)
            callArgs.Add(kwargs);

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "call",
            ["id"] = Interlocked.Increment(ref _rpcId),
            ["params"] = new JsonObject
            {
                ["service"] = "object",
                ["method"] = "execute_kw",
                ["args"] = callArgs
            }
        };

        return await SendRpcAsync("/jsonrpc", payload);
    }

    private async Task<JsonNode?> CallJsonRpcAsync(string path, JsonObject parameters)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "call",
            ["id"] = Interlocked.Increment(ref _rpcId),
            ["params"] = parameters
        };

        return await SendRpcAsync(path, payload);
    }

    private async Task<JsonNode?> SendRpcAsync(string path, JsonObject payload)
    {
        var url = _settings.BaseUrl.TrimEnd('/') + path;
        var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body);

        var error = json?["error"];
        if (error != null)
        {
            var errorMessage = error["data"]?["message"]?.GetValue<string>()
                ?? error["message"]?.GetValue<string>()
                ?? "Unknown Odoo RPC error";
            throw new InvalidOperationException($"Odoo RPC error: {errorMessage}");
        }

        return json?["result"];
    }
}
