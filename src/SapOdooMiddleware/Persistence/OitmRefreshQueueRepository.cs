using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>
/// The identity payload the DGX refresh worker reads from <c>before_value</c> /
/// <c>after_value</c>. The JSON key names are the worker's contract — do not rename.
/// </summary>
public sealed class OitmRefreshValues
{
    /// <summary>SAP U_Item_Name — the descriptive part name (NOT OITM.ItemName).</summary>
    [JsonPropertyName("item_name")] public string? ItemName { get; set; }
    /// <summary>SAP U_Article_No — the supplier/manufacturer article number.</summary>
    [JsonPropertyName("article_number")] public string? ArticleNumber { get; set; }
    /// <summary>The real brand of the part as held in SAP (U_ItemManufacturer, falling back to U_MdlTEST).</summary>
    [JsonPropertyName("brand")] public string? Brand { get; set; }
    /// <summary>SAP OITM.ItemName — the "/"-joined OEM chain.</summary>
    [JsonPropertyName("oem_chain")] public string? OemChain { get; set; }
}

/// <summary>
/// Publishes item-identity changes to <c>public.oitm_refresh_queue</c> in the Autohub Neon
/// (Parts_Catalog) database, AFTER the SAP write has committed. The DGX worker consumes the
/// rows (re-enrichment, images, fitment, …) — the middleware never calls the DGX for this and
/// never writes Neon <c>oitm</c> here. Failures must be swallowed by the caller: SAP is the
/// system of record and the nightly reconciliation catches missed rows.
/// </summary>
public interface IOitmRefreshQueueRepository
{
    /// <summary>
    /// Insert (or debounce-merge into) the open queue row for <paramref name="itemCode"/>.
    /// Idempotent per item: while a 'pending'/'running' row exists, changed fields are
    /// unioned and after_value/sap_update_ts/requested_by are replaced; before_value keeps
    /// the original pre-edit snapshot.
    /// </summary>
    Task EnqueueAsync(
        string itemCode, IReadOnlyCollection<string> changedFields,
        OitmRefreshValues? before, OitmRefreshValues after,
        DateTime? sapUpdateTs, string? requestedBy, CancellationToken ct);
}

/// <summary>
/// Singleton — resolves the Autohub tenant's Neon connection directly from
/// <c>Companies:Autohub:Neon</c> (the Item Master API lives under <c>/api/sap</c>, which the
/// URL-based tenant middleware does NOT map to Autohub, so ICompanyContext cannot be used here).
/// Only <c>oitm_refresh_queue</c> is written; status/attempts/result columns belong to the worker.
/// </summary>
public sealed class OitmRefreshQueueRepository : IOitmRefreshQueueRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly CompaniesOptions _companies;

    public OitmRefreshQueueRepository(IOptions<CompaniesOptions> companies)
        => _companies = companies.Value;

    private string ConnectionString
    {
        get
        {
            if (!_companies.Companies.TryGetValue(CompanyContext.AutohubKey, out var cfg)
                || string.IsNullOrWhiteSpace(cfg.Neon.ConnectionString))
            {
                throw new InvalidOperationException(
                    "Companies:Autohub:Neon:ConnectionString is not configured — required for the oitm refresh queue.");
            }
            return cfg.Neon.ConnectionString;
        }
    }

    public async Task EnqueueAsync(
        string itemCode, IReadOnlyCollection<string> changedFields,
        OitmRefreshValues? before, OitmRefreshValues after,
        DateTime? sapUpdateTs, string? requestedBy, CancellationToken ct)
    {
        if (changedFields.Count == 0)
            throw new ArgumentException("changedFields must not be empty.", nameof(changedFields));

        // Debounce: the partial unique index allows one open ('pending'/'running') row per
        // item, so repeat edits before the worker runs merge instead of duplicating.
        const string sql = """
            INSERT INTO oitm_refresh_queue
              (item_code, changed_fields, before_value, after_value, sap_update_ts, requested_by, origin)
            VALUES (@code, @fields, @before, @after, @sapUpdateTs, @user, 'inventory_app')
            ON CONFLICT (item_code) WHERE status IN ('pending','running') DO UPDATE
            SET changed_fields = ARRAY(SELECT DISTINCT unnest(
                    oitm_refresh_queue.changed_fields || EXCLUDED.changed_fields)),
                after_value   = EXCLUDED.after_value,
                sap_update_ts = EXCLUDED.sap_update_ts,
                requested_by  = EXCLUDED.requested_by,
                requested_at  = NOW();
            """;

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("code", Truncate(itemCode, 50)!);
        cmd.Parameters.Add(new NpgsqlParameter("fields", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = changedFields.Distinct(StringComparer.Ordinal).ToArray(),
        });
        cmd.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Jsonb)
        {
            Value = before is null ? DBNull.Value : JsonSerializer.Serialize(before, JsonOpts),
        });
        cmd.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(after, JsonOpts),
        });
        cmd.Parameters.Add(new NpgsqlParameter("sapUpdateTs", NpgsqlDbType.Timestamp)
        {
            Value = (object?)sapUpdateTs ?? DBNull.Value,
        });
        cmd.Parameters.AddWithValue("user",
            (object?)Truncate(requestedBy, 100) ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? Truncate(string? value, int max)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return v;
        return v.Length <= max ? v : v[..max];
    }
}
