using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services;

/// <summary>
/// Generic Odoo JSON-RPC client. The transport mirrors <see cref="OdooJsonRpcService"/> exactly
/// (Bearer <c>/json/2/{model}/{method}</c> when an API key is configured, classic
/// <c>/jsonrpc execute_kw</c> otherwise) so the two share the <c>Odoo</c> settings and the
/// <c>sap-integration</c> user. Kept separate so the purchases flow does not grow the
/// delivery/invoice/COGS service.
/// </summary>
public sealed class OdooRpcClient : IOdooRpc
{
    private readonly OdooSettings _settings;
    private readonly HttpClient _http;
    private readonly ILogger<OdooRpcClient> _logger;

    private int? _uid;
    private int _rpcId;

    public OdooRpcClient(IOptions<OdooSettings> settings, HttpClient http, ILogger<OdooRpcClient> logger)
    {
        _settings = settings.Value;
        _http = http;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // IOdooRpc
    // ------------------------------------------------------------------

    public async Task<List<int>> SearchAsync(string model, JsonArray domain, int? limit = null, string? order = null)
    {
        var kwargs = new JsonObject();
        if (limit.HasValue) kwargs["limit"] = limit.Value;
        if (!string.IsNullOrEmpty(order)) kwargs["order"] = order;

        JsonNode? result;
        if (_settings.UseBearerAuth)
        {
            var body = new JsonObject { ["domain"] = domain.DeepClone() };
            foreach (var kv in kwargs) body[kv.Key] = kv.Value?.DeepClone();
            result = await SendJson2Async(model, "search", body);
        }
        else
        {
            await EnsureAuthenticatedAsync();
            result = await CallObjectMethodAsync(model, "search", new JsonArray { domain.DeepClone() }, kwargs);
        }

        return result?.AsArray().Select(n => n!.GetValue<int>()).ToList() ?? [];
    }

    public async Task<List<JsonObject>> SearchReadAsync(
        string model, JsonArray domain, IEnumerable<string> fields, int? limit = null, string? order = null)
    {
        var fieldArray = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        var kwargs = new JsonObject { ["fields"] = fieldArray };
        if (limit.HasValue) kwargs["limit"] = limit.Value;
        if (!string.IsNullOrEmpty(order)) kwargs["order"] = order;

        JsonNode? result;
        if (_settings.UseBearerAuth)
        {
            var body = new JsonObject { ["domain"] = domain.DeepClone() };
            foreach (var kv in kwargs) body[kv.Key] = kv.Value?.DeepClone();
            result = await SendJson2Async(model, "search_read", body);
        }
        else
        {
            await EnsureAuthenticatedAsync();
            result = await CallObjectMethodAsync(model, "search_read", new JsonArray { domain.DeepClone() }, kwargs);
        }

        return result?.AsArray().Select(n => n!.AsObject()).ToList() ?? [];
    }

    public async Task<JsonObject?> ReadAsync(string model, int id, IEnumerable<string> fields)
    {
        var fieldArray = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        JsonNode? result;
        if (_settings.UseBearerAuth)
        {
            result = await SendJson2Async(model, "read", new JsonObject
            {
                ["ids"] = new JsonArray { id },
                ["fields"] = fieldArray
            });
        }
        else
        {
            await EnsureAuthenticatedAsync();
            result = await CallObjectMethodAsync(model, "read", new JsonArray { new JsonArray { id }, fieldArray });
        }

        return result?.AsArray().FirstOrDefault()?.AsObject();
    }

    public async Task<int> CreateAsync(string model, JsonObject values)
    {
        if (_settings.UseBearerAuth)
        {
            var result = await SendJson2Async(model, "create", new JsonObject
            {
                ["vals_list"] = new JsonArray { values.DeepClone() }
            });
            if (result is JsonArray arr && arr.Count > 0)
                return arr[0]!.GetValue<int>();
            return result?.GetValue<int>()
                ?? throw new InvalidOperationException($"Odoo create on {model} returned null.");
        }

        await EnsureAuthenticatedAsync();
        var classic = await CallObjectMethodAsync(model, "create", new JsonArray { values.DeepClone() });
        return classic?.GetValue<int>()
            ?? throw new InvalidOperationException($"Odoo create on {model} returned null.");
    }

    public async Task WriteAsync(string model, IEnumerable<int> ids, JsonObject values)
    {
        var idArray = new JsonArray(ids.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        if (_settings.UseBearerAuth)
        {
            await SendJson2Async(model, "write", new JsonObject
            {
                ["ids"] = idArray,
                ["vals"] = values.DeepClone()
            });
            return;
        }

        await EnsureAuthenticatedAsync();
        await CallObjectMethodAsync(model, "write", new JsonArray { idArray, values.DeepClone() });
    }

    public async Task<JsonNode?> CallAsync(string model, string method, IEnumerable<int> ids, JsonObject? kwargs = null)
    {
        var idArray = new JsonArray(ids.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        if (_settings.UseBearerAuth)
        {
            var body = new JsonObject { ["ids"] = idArray };
            if (kwargs != null)
                foreach (var kv in kwargs) body[kv.Key] = kv.Value?.DeepClone();
            return await SendJson2Async(model, method, body);
        }

        await EnsureAuthenticatedAsync();
        return await CallObjectMethodAsync(model, method, new JsonArray { idArray }, kwargs?.DeepClone().AsObject());
    }

    public async Task<JsonNode?> CallModelAsync(string model, string method, JsonObject? kwargs = null)
    {
        if (_settings.UseBearerAuth)
            return await SendJson2Async(model, method, kwargs?.DeepClone().AsObject() ?? new JsonObject());

        await EnsureAuthenticatedAsync();
        return await CallObjectMethodAsync(model, method, new JsonArray(), kwargs?.DeepClone().AsObject());
    }

    public async Task<bool> ModelExistsAsync(string model)
    {
        var ids = await SearchAsync("ir.model", new JsonArray
        {
            new JsonArray { "model", "=", model }
        }, limit: 1);
        return ids.Count > 0;
    }

    // ------------------------------------------------------------------
    // Transport (same shape as OdooJsonRpcService)
    // ------------------------------------------------------------------

    private async Task EnsureAuthenticatedAsync()
    {
        if (_uid.HasValue) return;

        var result = await SendRpcAsync("/web/session/authenticate", new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "call",
            ["id"] = Interlocked.Increment(ref _rpcId),
            ["params"] = new JsonObject
            {
                ["db"] = _settings.Database,
                ["login"] = _settings.UserName,
                ["password"] = _settings.Password
            }
        });

        _uid = result?["uid"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Odoo authentication failed — uid is null.");
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
        foreach (var arg in args)
            callArgs.Add(arg?.DeepClone());
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

    private async Task<JsonNode?> SendJson2Async(string model, string method, JsonObject body)
    {
        var url = _settings.BaseUrl.TrimEnd('/') + $"/json/2/{model}/{method}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Authorization", $"Bearer {_settings.EffectiveApiKey}");
        request.Headers.Add("X-Odoo-Database", _settings.Database);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        _logger.LogDebug("Odoo /json/2/ {Model}.{Method}", model, method);

        using var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Odoo /json/2/ error {StatusCode} on {Model}.{Method}: {Response}",
                (int)response.StatusCode, model, method, Truncate(responseBody, 2000));
            throw new OdooRpcException(ExtractError(responseBody, (int)response.StatusCode));
        }

        return string.IsNullOrWhiteSpace(responseBody) ? null : JsonNode.Parse(responseBody);
    }

    private async Task<JsonNode?> SendRpcAsync(string path, JsonObject payload)
    {
        var url = _settings.BaseUrl.TrimEnd('/') + path;
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body);
        var error = json?["error"];
        if (error != null)
        {
            var message = error["data"]?["message"]?.GetValue<string>()
                ?? error["message"]?.GetValue<string>()
                ?? "Unknown Odoo RPC error";
            throw new OdooRpcException(message);
        }
        return json?["result"];
    }

    private static string ExtractError(string responseBody, int status)
    {
        try
        {
            var json = JsonNode.Parse(responseBody);
            var message = json?["error"]?["data"]?["message"]?.GetValue<string>()
                ?? json?["error"]?["message"]?.GetValue<string>()
                ?? json?["message"]?.GetValue<string>()
                ?? json?["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message))
                return message;
        }
        catch (JsonException)
        {
            // fall through
        }
        return $"HTTP {status}: {Truncate(responseBody, 500)}";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>An error returned by Odoo (validation, access, missing model/field).</summary>
public sealed class OdooRpcException : Exception
{
    public OdooRpcException(string message) : base(message) { }
}
