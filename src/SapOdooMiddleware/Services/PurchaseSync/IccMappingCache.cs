using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.PurchaseSync;

/// <summary>Singleton settings record of the control centre (<c>icc.ap.sync.settings</c>).</summary>
public sealed record IccApSyncSettings(
    int Id,
    decimal TotalTolerance,
    DateOnly? CutoverDate,
    int? BillJournalId,
    decimal SmallBalanceLimit);

/// <summary>
/// The control-centre mapping tables the SAP → Odoo bill sync needs (spec v4 §5.7), keyed the way
/// the SAP documents present them.
/// </summary>
public sealed class IccMappings
{
    public required IccApSyncSettings Settings { get; init; }

    /// <summary>SAP VatGroup (e.g. "I1") → account.tax id.</summary>
    public required IReadOnlyDictionary<string, int> TaxByVatGroup { get; init; }

    /// <summary>OITB.ItmsGrpCod → (account.account id, analytic account id or null).</summary>
    public required IReadOnlyDictionary<int, (int AccountId, int? AnalyticAccountId)> AccountByItemGroup { get; init; }

    /// <summary>SAP G/L account code → account.account id.</summary>
    public required IReadOnlyDictionary<string, int> AccountByGlCode { get; init; }

    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface IIccMappingCache
{
    /// <summary>Returns the cached mappings, loading them from Odoo when the cache is cold or expired.</summary>
    Task<IccMappings> GetAsync(CancellationToken ct);

    /// <summary>Drops the cache so the next call reloads (after accounts adds a mapping).</summary>
    void Invalidate();
}

/// <summary>
/// Loads <c>icc.ap.sync.settings</c>, <c>icc.sap.vatgroup.map</c>, <c>icc.sap.itemgroup.map</c> and
/// <c>icc.sap.glaccount.map</c> over JSON-RPC and caches them for <c>PurchaseSync:MappingCacheMinutes</c>.
/// </summary>
public sealed class IccMappingCache : IIccMappingCache
{
    private const string CacheKey = "purchase-sync:icc-mappings";

    private readonly IOdooRpc _odoo;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<PurchaseSyncSettings> _settings;
    private readonly ILogger<IccMappingCache> _logger;

    public IccMappingCache(
        IOdooRpc odoo,
        IMemoryCache cache,
        IOptionsMonitor<PurchaseSyncSettings> settings,
        ILogger<IccMappingCache> logger)
    {
        _odoo = odoo;
        _cache = cache;
        _settings = settings;
        _logger = logger;
    }

    public void Invalidate() => _cache.Remove(CacheKey);

    public async Task<IccMappings> GetAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKey, out IccMappings? cached) && cached != null)
            return cached;

        var loaded = await LoadAsync(ct);
        var minutes = Math.Max(1, _settings.CurrentValue.MappingCacheMinutes);
        _cache.Set(CacheKey, loaded, TimeSpan.FromMinutes(minutes));
        return loaded;
    }

    private async Task<IccMappings> LoadAsync(CancellationToken ct)
    {
        var s = _settings.CurrentValue;

        List<JsonObject> settingsRows;
        try
        {
            settingsRows = await _odoo.SearchReadAsync(
                s.SettingsModel, new JsonArray(),
                ["id", "total_tolerance", "cutover_date", "bill_journal_id", "small_balance_limit"],
                limit: 1);
        }
        catch (OdooRpcException ex)
        {
            throw new PurchaseSyncException(
                "ICC_SETTINGS_MISSING",
                $"Cannot read {s.SettingsModel} from Odoo ({ex.Message}). Install the control-centre " +
                "purchases models (spec §5.7) and grant the integration user read access.",
                retryable: true);
        }

        if (settingsRows.Count == 0)
            throw new PurchaseSyncException(
                "ICC_SETTINGS_MISSING",
                $"No {s.SettingsModel} record exists. Create it under Integration → Configuration → SAP Purchases.",
                retryable: true);

        var row = settingsRows[0];
        var settings = new IccApSyncSettings(
            Id: row["id"]!.GetValue<int>(),
            TotalTolerance: OdooValues.Decimal(row["total_tolerance"], 0.01m),
            CutoverDate: OdooValues.Date(row["cutover_date"]),
            BillJournalId: OdooValues.Many2OneId(row["bill_journal_id"]),
            SmallBalanceLimit: OdooValues.Decimal(row["small_balance_limit"], 10m));

        var vat = await _odoo.SearchReadAsync(
            s.VatGroupMapModel, new JsonArray(), ["sap_vat_group", "tax_id"]);
        var taxByVatGroup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vat)
        {
            var key = OdooValues.String(v["sap_vat_group"]);
            var taxId = OdooValues.Many2OneId(v["tax_id"]);
            if (!string.IsNullOrWhiteSpace(key) && taxId.HasValue)
                taxByVatGroup[key.Trim()] = taxId.Value;
        }

        var groups = await _odoo.SearchReadAsync(
            s.ItemGroupMapModel, new JsonArray(), ["sap_item_group", "account_id", "analytic_account_id"]);
        var accountByItemGroup = new Dictionary<int, (int, int?)>();
        foreach (var g in groups)
        {
            var code = OdooValues.Int(g["sap_item_group"]);
            var accountId = OdooValues.Many2OneId(g["account_id"]);
            if (code.HasValue && accountId.HasValue)
                accountByItemGroup[code.Value] = (accountId.Value, OdooValues.Many2OneId(g["analytic_account_id"]));
        }

        var gls = await _odoo.SearchReadAsync(
            s.GlAccountMapModel, new JsonArray(), ["sap_gl_code", "account_id"]);
        var accountByGl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in gls)
        {
            var code = OdooValues.String(g["sap_gl_code"]);
            var accountId = OdooValues.Many2OneId(g["account_id"]);
            if (!string.IsNullOrWhiteSpace(code) && accountId.HasValue)
                accountByGl[code.Trim()] = accountId.Value;
        }

        _logger.LogInformation(
            "ICC purchase mappings loaded: {Vat} VAT groups, {Groups} item groups, {Gl} G/L codes, " +
            "cut-over={Cutover}, journal={Journal}, tolerance={Tolerance}",
            taxByVatGroup.Count, accountByItemGroup.Count, accountByGl.Count,
            settings.CutoverDate?.ToString("yyyy-MM-dd") ?? "(not set)", settings.BillJournalId, settings.TotalTolerance);

        return new IccMappings
        {
            Settings = settings,
            TaxByVatGroup = taxByVatGroup,
            AccountByItemGroup = accountByItemGroup,
            AccountByGlCode = accountByGl,
        };
    }
}

/// <summary>Readers for the shapes Odoo returns (many2one = [id, name] or false; dates as strings).</summary>
public static class OdooValues
{
    public static int? Many2OneId(JsonNode? node)
    {
        if (node is JsonArray arr && arr.Count > 0 && arr[0] is JsonValue v && v.TryGetValue<int>(out var id))
            return id;
        if (node is JsonValue single && single.TryGetValue<int>(out var plain))
            return plain;
        return null;
    }

    public static string? String(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out _)) return null;   // Odoo's False for an empty Char
        }
        return null;
    }

    public static int? Int(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)d;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    public static decimal Decimal(JsonNode? node, decimal fallback)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<decimal>(out var m)) return m;
            if (v.TryGetValue<double>(out var d)) return (decimal)d;
            if (v.TryGetValue<int>(out var i)) return i;
        }
        return fallback;
    }

    public static DateOnly? Date(JsonNode? node)
    {
        var s = String(node);
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateOnly.TryParseExact(s[..Math.Min(10, s.Length)], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
            return d;
        return null;
    }

    public static string Date(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
