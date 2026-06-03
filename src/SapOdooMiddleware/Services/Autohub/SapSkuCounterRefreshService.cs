using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>Per-prefix outcome of a SKU counter refresh, for operator visibility.</summary>
public sealed record SkuRefreshResult(string Prefix, long NeonWas, long SapMaxConsecutive, long NeonNew);

public interface ISapSkuCounterRefreshService
{
    /// <summary>
    /// For every prefix in sku_counters: query the live SAP MAX of the contiguous ItemCode sequence
    /// and bump the Neon counter to max(neon, sap) — never backwards. Returns per-prefix old/new.
    /// </summary>
    Task<IReadOnlyList<SkuRefreshResult>> RefreshAllAsync(CancellationToken ct);
}

/// <summary>
/// Keeps the atomic Neon sku_counters in step with SAP without manual seeding. Neon stays the hot
/// allocation path; this job pulls the authoritative MAX from SAP (OITM) so counters self-heal.
///
/// The SAP query uses LAG-based gap detection to ignore test outliers (e.g. VAG9999, VAG20000+):
/// it walks the per-prefix numeric suffixes in order and stops at the first jump bigger than the
/// gap threshold, returning the last value of the contiguous run that starts near 1.
///
/// Reads the Autohub tenant config directly (not ICompanyContext) so it can run as a singleton from
/// both the background timer and the /api/admin endpoint regardless of request routing.
/// </summary>
public sealed class SapSkuCounterRefreshService : ISapSkuCounterRefreshService
{
    private readonly CompaniesOptions _companies;
    private readonly AutohubSkuRefreshSettings _settings;
    private readonly ILogger<SapSkuCounterRefreshService> _logger;

    public SapSkuCounterRefreshService(
        IOptions<CompaniesOptions> companies,
        IOptions<AutohubSkuRefreshSettings> settings,
        ILogger<SapSkuCounterRefreshService> logger)
    {
        _companies = companies.Value;
        _settings = settings.Value;
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
        foreach (var (prefix, neonValue) in prefixes)
        {
            long sapMax;
            try
            {
                sapMax = await QuerySapMaxConsecutiveAsync(sap, prefix, GapThresholdFor(prefix), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SKU refresh: SAP query failed for prefix={Prefix}; leaving Neon counter unchanged.", prefix);
                continue;
            }

            var newValue = Math.Max(neonValue, sapMax);
            if (newValue > neonValue)
                await BumpCounterAsync(neonConn, prefix, newValue, ct);

            _logger.LogInformation(
                "SKU refresh: prefix={Prefix}, neon_was={NeonWas}, sap_max_consecutive={SapMax}, neon_new={NeonNew}",
                prefix, neonValue, sapMax, newValue);
            results.Add(new SkuRefreshResult(prefix, neonValue, sapMax, newValue));
        }

        return results;
    }

    private int GapThresholdFor(string prefix) =>
        _settings.GapThresholdByPrefix.TryGetValue(prefix, out var t) ? t : _settings.DefaultGapThreshold;

    private static string BuildSapConnectionString(SapB1Settings sap) =>
        new SqlConnectionStringBuilder
        {
            DataSource = sap.Server,
            InitialCatalog = sap.CompanyDb,
            UserID = sap.UserName,
            Password = sap.Password,
            TrustServerCertificate = true,
        }.ConnectionString;

    private static async Task<List<(string Prefix, long Value)>> ReadPrefixesAsync(string neonConn, CancellationToken ct)
    {
        const string sql = """SELECT "Prefix", "CurrentValue" FROM sku_counters;""";
        await using var conn = new NpgsqlConnection(neonConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var list = new List<(string, long)>();
        while (await reader.ReadAsync(ct))
            list.Add((reader.GetString(0), reader.GetInt64(1)));
        return list;
    }

    private static async Task BumpCounterAsync(string neonConn, string prefix, long newValue, CancellationToken ct)
    {
        // Guard the write with the same never-backwards rule in case another caller raced ahead.
        const string sql = """
            UPDATE sku_counters
            SET "CurrentValue" = @value, "LastUpdated" = NOW()
            WHERE "Prefix" = @prefix AND "CurrentValue" < @value;
            """;
        await using var conn = new NpgsqlConnection(neonConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("prefix", prefix);
        cmd.Parameters.AddWithValue("value", newValue);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> QuerySapMaxConsecutiveAsync(SqlConnection sap, string prefix, int gapThreshold, CancellationToken ct)
    {
        // TRY_CAST guards against non-numeric suffixes (e.g. "VAG-OLD"); the LIKE only guarantees the
        // first post-prefix char is a digit. The contiguous-run logic is the operator's gap filter.
        const string sql = """
            WITH numbered AS (
                SELECT TRY_CAST(SUBSTRING(ItemCode, @prefixLen + 1, LEN(ItemCode)) AS BIGINT) AS num
                FROM OITM
                WHERE ItemCode LIKE @pattern
            ),
            valid AS (
                SELECT num FROM numbered WHERE num IS NOT NULL
            ),
            with_gaps AS (
                SELECT num, num - LAG(num, 1, num - 1) OVER (ORDER BY num) AS gap
                FROM valid
            )
            SELECT COALESCE(
                (SELECT MAX(num) FROM valid
                 WHERE num < (SELECT MIN(num) FROM with_gaps WHERE gap > @gap AND num > 100)),
                (SELECT MAX(num) FROM valid),
                0
            );
            """;
        await using var cmd = new SqlCommand(sql, sap);
        cmd.Parameters.AddWithValue("@prefixLen", prefix.Length);
        cmd.Parameters.AddWithValue("@pattern", prefix + "[0-9]%");
        cmd.Parameters.AddWithValue("@gap", gapThreshold);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }
}
