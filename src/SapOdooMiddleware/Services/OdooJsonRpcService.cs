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
        if (!_settings.UseBearerAuth)
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

        // 4. Set qty_done = quantity (demand) on each move line
        //    In Odoo 18, action_set_quantities_to_reservation does not exist.
        //    Instead, we read each stock.move.line, get its 'quantity' (demand),
        //    and write it to 'qty_done', plus set 'picked' = true.
        var moveLineIds = await SearchAsync("stock.move.line", new JsonArray
        {
            new JsonArray { JsonValue.Create("picking_id"), JsonValue.Create("="), JsonValue.Create(pickingId) }
        });

        if (moveLineIds.Count > 0)
        {
            foreach (var mlId in moveLineIds)
            {
                // Read the demand quantity
                var mlData = await ReadAsync("stock.move.line", mlId, new JsonArray
                {
                    JsonValue.Create("quantity")
                });

                var demandQty = mlData?["quantity"]?.GetValue<double>() ?? 0;

                // Write qty_done = demand quantity, picked = true
                await WriteAsync("stock.move.line", mlId, new JsonObject
                {
                    ["qty_done"] = demandQty,
                    ["picked"] = true
                });
            }
        }
        _logger.LogInformation("Set qty_done on {Count} move lines for picking id={PickingId}", moveLineIds.Count, pickingId);

        // 5. button_validate() — with context to skip backorder/immediate-transfer wizards
        await ExecuteMethodWithContextAsync("stock.picking", "button_validate", new JsonArray { pickingId },
            new JsonObject
            {
                ["skip_backorder"] = true,
                ["skip_immediate"] = true
            });
        _logger.LogInformation("Validated picking id={PickingId}", pickingId);

        // 6. Write SAP delivery DocEntry and date onto the picking
        var writeValues = new JsonObject();

        // x_sap_delivery_docentry is an integer field in Odoo
        if (int.TryParse(request.SapDeliveryNo, out int deliveryDocEntry))
            writeValues["x_sap_delivery_docentry"] = deliveryDocEntry;
        else
            writeValues["x_sap_delivery_docentry"] = request.SapDeliveryNo;

        // x_sap_delivery_date is a datetime field in Odoo
        if (request.DeliveryDate.HasValue)
            writeValues["x_sap_delivery_date"] = request.DeliveryDate.Value.ToString("yyyy-MM-dd HH:mm:ss");

        await WriteAsync("stock.picking", pickingId, writeValues);
        _logger.LogInformation("Wrote SAP delivery DocEntry={DocEntry} onto picking id={PickingId}", request.SapDeliveryNo, pickingId);

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

    public async Task<OdooPingResponse> PingAsync()
    {
        if (_settings.UseBearerAuth)
        {
            // Test connectivity with a safe no-op call using the /json/2/ API
            await SendJson2Async("res.partner", "search_read", new JsonObject
            {
                ["domain"] = new JsonArray
                {
                    new JsonArray { JsonValue.Create("id"), JsonValue.Create("="), JsonValue.Create(0) }
                },
                ["fields"] = new JsonArray { JsonValue.Create("id") },
                ["limit"] = 1
            });

            return new OdooPingResponse
            {
                Connected = true,
                Uid = 0,
                Database = _settings.Database,
                ServerVersion = null,
                BaseUrl = _settings.BaseUrl,
                UserName = _settings.UserName
            };
        }

        // Classic session auth mode
        _uid = null;
        await EnsureAuthenticatedAsync();

        // Optionally get server version via /web/session/get_session_info
        string? serverVersion = null;
        try
        {
            var versionResult = await CallJsonRpcAsync("/web/session/get_session_info", new JsonObject());
            serverVersion = versionResult?["server_version"]?.GetValue<string>();
        }
        catch
        {
            // Version info is optional — don't fail the ping
        }

        return new OdooPingResponse
        {
            Connected = _uid.HasValue,
            Uid = _uid ?? 0,
            Database = _settings.Database,
            ServerVersion = serverVersion,
            BaseUrl = _settings.BaseUrl,
            UserName = _settings.UserName
        };
    }

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
        if (_settings.UseBearerAuth)
        {
            var result = await SendJson2Async(model, "search", new JsonObject { ["domain"] = domain });
            return result?.AsArray().Select(n => n!.GetValue<int>()).ToList() ?? [];
        }

        var classicResult = await CallObjectMethodAsync(model, "search", new JsonArray { domain });
        return classicResult?.AsArray().Select(n => n!.GetValue<int>()).ToList() ?? [];
    }

    private async Task ExecuteMethodAsync(string model, string method, JsonArray ids)
    {
        if (_settings.UseBearerAuth)
        {
            await SendJson2Async(model, method, new JsonObject { ["ids"] = ids });
            return;
        }

        await CallObjectMethodAsync(model, method, new JsonArray { ids });
    }

    private async Task ExecuteMethodWithContextAsync(string model, string method, JsonArray ids, JsonObject context)
    {
        if (_settings.UseBearerAuth)
        {
            await SendJson2Async(model, method, new JsonObject { ["ids"] = ids, ["context"] = context });
            return;
        }

        var kwargs = new JsonObject { ["context"] = context };
        await CallObjectMethodAsync(model, method, new JsonArray { ids }, kwargs);
    }

    private async Task WriteAsync(string model, int id, JsonObject values)
    {
        if (_settings.UseBearerAuth)
        {
            await SendJson2Async(model, "write", new JsonObject { ["ids"] = new JsonArray { id }, ["vals"] = values });
            return;
        }

        await CallObjectMethodAsync(model, "write", new JsonArray
        {
            new JsonArray { id },
            values
        });
    }

    private async Task<JsonObject?> ReadAsync(string model, int id, JsonArray fields)
    {
        if (_settings.UseBearerAuth)
        {
            var result = await SendJson2Async(model, "read", new JsonObject { ["ids"] = new JsonArray { id }, ["fields"] = fields });
            return result?.AsArray().FirstOrDefault()?.AsObject();
        }

        var classicResult = await CallObjectMethodAsync(model, "read", new JsonArray
        {
            new JsonArray { id },
            fields
        });

        return classicResult?.AsArray().FirstOrDefault()?.AsObject();
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

    private async Task<JsonNode?> SendJson2Async(string model, string method, JsonObject body)
    {
        var url = _settings.BaseUrl.TrimEnd('/') + $"/json/2/{model}/{method}";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Authorization", $"Bearer {_settings.EffectiveApiKey}");
        request.Headers.Add("X-Odoo-Database", _settings.Database);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        _logger.LogDebug("Odoo /json/2/ request: {Method} {Url} Body: {Body}", "POST", url, body.ToJsonString());

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Odoo /json/2/ error: {StatusCode} {Url} Response: {Response}",
                (int)response.StatusCode, url, responseBody);

            string errorMessage;
            try
            {
                var errorJson = JsonNode.Parse(responseBody);
                errorMessage = errorJson?["error"]?["data"]?["message"]?.GetValue<string>()
                    ?? errorJson?["error"]?["message"]?.GetValue<string>()
                    ?? errorJson?["error"]?.GetValue<string>()
                    ?? $"HTTP {(int)response.StatusCode}";
            }
            catch (JsonException)
            {
                errorMessage = $"HTTP {(int)response.StatusCode}: {responseBody}";
            }
            throw new InvalidOperationException($"Odoo API error: {errorMessage}");
        }

        return JsonNode.Parse(responseBody);
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
