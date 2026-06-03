using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>Per-prefix outcome of a SKU counter refresh, for operator visibility.</summary>
public sealed record SkuRefreshResult(
    string Prefix,
    long NeonWas,
    long SapMaxFiltered,
    long NeonNew,
    long? MaxAllowed,
    int AboveCeilingCount);

public interface ISapSkuCounterRefreshService
{
    /// <summary>
    /// For every prefix in sku_counters: query the live SAP MAX of the ItemCode suffix capped at the
    /// per-prefix MaxAllowed ceiling, and bump the Neon counter to max(neon, sap) — never backwards.
    /// Returns per-prefix old/new plus a count of items above the ceiling (for operator review).
    /// </summary>
    Task<IReadOnlyList<SkuRefreshResult>> RefreshAllAsync(CancellationToken ct);
}

/// <summary>
/// Keeps the atomic Neon sku_counters in step with SAP without manual seeding. Neon stays the hot
/// allocation path; this job pulls the authoritative MAX from SAP (OITM) so counters self-heal.
///
/// Outlier handling is a deterministic, operator-controlled CEILING, not gap detection. Real OITM
/// data has outlier spacings and legitimate internal gaps that no single gap threshold can separate
/// (e.g. MB outliers at gap=1496, VAG outliers at gap=4446, but a *legit* VAG gap of 1228). Instead,
/// each prefix carries sku_counters.MaxAllowed: the MAX query ignores any suffix above it, so test
/// items parked above the ceiling never inflate the counter. When new items become legitimate the
/// operator just raises MaxAllowed. A diagnostic logs how many items currently sit above the ceiling.
///
/// Reads the Autohub tenant config directly (not ICompanyContext) so it can run as a singleton from
/// both the background timer and the /api/admin endpoint regardless of request routing.
/// </summary>
public sealed class SapSkuCounterRefreshService : ISapSkuCounterRefreshService
{
    private const long NoCeiling = 9_999_999_999L;

    private readonly CompaniesOptions _companies;
    private readonly ILogger<SapSkuCounterRefreshService> _logger;

    public SapSkuCounterRefreshService(
        IOptions<CompaniesOptions> companies,
        ILogger<SapSkuCounterRefreshService> logger)
    {
        _companies = companies.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SkuRefreshResult>> RefreshAllAsync(CancellationToken ct)
    {
        if (!_companies.Companies.TryGetValue(CompanyContext.AutohubKey, out var cfg))
        {
            _logger.LogWarning("SKU refresh: no Autohub tenant configured; nothing to do.");
            return Array.Empty<SkuRefreshResult>();
        }
        if (cfg.SapB1 is null || string.IsNullOrWhiteSpace(cfg.SapB1.Server) || string.IsNullOrWhiteSpace(cfg.SapB1.CompanyDb))
        {
            _logger.LogWarning("SKU refresh: Autohub SapB1 connection is not configured; skipping.");
            return Array.Empty<SkuRefreshResult>();
        }
        if (!cfg.SapB1.DbServerType.Contains("MSSQL", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("SKU refresh: only MSSQL is supported (DbServerType={Type}); skipping.", cfg.SapB1.DbServerType);
            return Array.Empty<SkuRefreshResult>();
        }

        var neonConn = cfg.Neon.ConnectionString;
        var sapConn = BuildSapConnectionString(cfg.SapB1);

        var prefixes = await ReadPrefixesAsync(neonConn, ct);
        if (prefixes.Count == 0)
        {
            _logger.LogInformation("SKU refresh: sku_counters is empty; nothing to refresh.");
            return Array.Empty<SkuRefreshResult>();
        }

        await using var sap = new SqlConnection(sapConn);
        await sap.OpenAsync(ct);

        var results = new List<SkuRefreshResult>(prefixes.Count);
        foreach (var (prefix, neonValue, maxAllowed) in prefixes)
        {
            long sapMax;
            int aboveCeiling;
            try
            {
                sapMax = await QuerySapMaxFilteredAsync(sap, prefix, maxAllowed, ct);
                aboveCeiling = maxAllowed is null ? 0 : await QueryCountAboveCeilingAsync(sap, prefix, maxAllowed.Value, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SKU refresh: SAP query failed for prefix={Prefix}; leaving Neon counter unchanged.", prefix);
                continue;
            }

            if (aboveCeiling > 0)
                _logger.LogWarning(
                    "SKU refresh: found {Count} item(s) above MaxAllowed ({MaxAllowed}) for prefix {Prefix} — review and bump MaxAllowed if these are now legitimate.",
                    aboveCeiling, maxAllowed, prefix);

            var newValue = Math.Max(neonValue, sapMax);
            if (newValue > neonValue)
                await BumpCounterAsync(neonConn, prefix, sapMax, ct);

            _logger.LogInformation(
                "SKU refresh: prefix={Prefix}, neon_was={NeonWas}, sap_max_filtered={SapMax}, neon_new={NeonNew}, max_allowed={MaxAllowed}",
                prefix, neonValue, sapMax, newValue, maxAllowed);
            results.Add(new SkuRefreshResult(prefix, neonValue, sapMax, newValue, maxAllowed, aboveCeiling));
        }

        return results;
    }

    private static string BuildSapConnectionString(SapB1Settings sap) =>
        new SqlConnectionStringBuilder
        {
            DataSource = sap.Server,
            InitialCatalog = sap.CompanyDb,
            UserID = sap.UserName,
            Password = sap.Password,
            TrustServerCertificate = true,
        }.ConnectionString;

    private static async Task<List<(string Prefix, long Value, long? MaxAllowed)>> ReadPrefixesAsync(string neonConn, CancellationToken ct)
    {
        const string sql = """SELECT "Prefix", "CurrentValue", "MaxAllowed" FROM sku_counters;""";
        await using var conn = new NpgsqlConnection(neonConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var list = new List<(string, long, long?)>();
        while (await reader.ReadAsync(ct))
        {
            long? maxAllowed = await reader.IsDBNullAsync(2, ct) ? null : reader.GetInt64(2);
            list.Add((reader.GetString(0), reader.GetInt64(1), maxAllowed));
        }
        return list;
    }

    private static async Task BumpCounterAsync(string neonConn, string prefix, long sapMax, CancellationToken ct)
    {
        // GREATEST guarantees the counter never moves backwards, even if another caller raced ahead.
        const string sql = """
            UPDATE sku_counters
            SET "CurrentValue" = GREATEST("CurrentValue", @sap), "LastUpdated" = NOW()
            WHERE "Prefix" = @prefix;
            """;
        await using var conn = new NpgsqlConnection(neonConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("prefix", prefix);
        cmd.Parameters.AddWithValue("sap", sapMax);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> QuerySapMaxFilteredAsync(SqlConnection sap, string prefix, long? maxAllowed, CancellationToken ct)
    {
        // Deterministic: the MAX of the numeric suffix, capped at the per-prefix ceiling. TRY_CAST
        // guards non-numeric suffixes (the LIKE only guarantees the first post-prefix char is a digit).
        const string sql = """
            SELECT MAX(TRY_CAST(SUBSTRING(ItemCode, @prefixLen + 1, LEN(ItemCode)) AS BIGINT))
            FROM OITM
            WHERE ItemCode LIKE @pattern
              AND TRY_CAST(SUBSTRING(ItemCode, @prefixLen + 1, LEN(ItemCode)) AS BIGINT) <= COALESCE(@maxAllowed, @noCeiling);
            """;
        await using var cmd = new SqlCommand(sql, sap);
        cmd.Parameters.AddWithValue("@prefixLen", prefix.Length);
        cmd.Parameters.AddWithValue("@pattern", prefix + "[0-9]%");
        cmd.Parameters.Add("@maxAllowed", System.Data.SqlDbType.BigInt).Value = (object?)maxAllowed ?? DBNull.Value;
        cmd.Parameters.AddWithValue("@noCeiling", NoCeiling);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private static async Task<int> QueryCountAboveCeilingAsync(SqlConnection sap, string prefix, long maxAllowed, CancellationToken ct)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM OITM
            WHERE ItemCode LIKE @pattern
              AND TRY_CAST(SUBSTRING(ItemCode, @prefixLen + 1, LEN(ItemCode)) AS BIGINT) > @maxAllowed;
            """;
        await using var cmd = new SqlCommand(sql, sap);
        cmd.Parameters.AddWithValue("@prefixLen", prefix.Length);
        cmd.Parameters.AddWithValue("@pattern", prefix + "[0-9]%");
        cmd.Parameters.AddWithValue("@maxAllowed", maxAllowed);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }
}
