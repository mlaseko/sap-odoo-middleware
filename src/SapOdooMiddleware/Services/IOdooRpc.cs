using System.Text.Json.Nodes;

namespace SapOdooMiddleware.Services;

/// <summary>
/// Thin, model-agnostic Odoo JSON-RPC surface (search / search_read / read / create / write / call)
/// for flows that need arbitrary models, such as the purchases sync. Uses the same credentials and
/// auth modes as <see cref="OdooJsonRpcService"/> (Bearer API key on <c>/json/2/</c> when
/// <c>Odoo:ApiKey</c> is set, classic session auth otherwise).
/// </summary>
public interface IOdooRpc
{
    Task<List<int>> SearchAsync(string model, JsonArray domain, int? limit = null, string? order = null);

    Task<List<JsonObject>> SearchReadAsync(
        string model, JsonArray domain, IEnumerable<string> fields, int? limit = null, string? order = null);

    Task<JsonObject?> ReadAsync(string model, int id, IEnumerable<string> fields);

    Task<int> CreateAsync(string model, JsonObject values);

    Task WriteAsync(string model, IEnumerable<int> ids, JsonObject values);

    /// <summary>
    /// Calls a recordset method (<c>self</c> = the given ids) with keyword arguments only. The
    /// <c>/json/2/</c> API binds arguments by name, so positional arguments are not supported.
    /// </summary>
    Task<JsonNode?> CallAsync(string model, string method, IEnumerable<int> ids, JsonObject? kwargs = null);

    /// <summary>Calls an <c>@api.model</c> method (no ids) with keyword arguments only.</summary>
    Task<JsonNode?> CallModelAsync(string model, string method, JsonObject? kwargs = null);

    /// <summary>Returns true when the model exists on the instance (ir.model lookup).</summary>
    Task<bool> ModelExistsAsync(string model);
}
