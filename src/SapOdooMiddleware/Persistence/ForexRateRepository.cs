using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

public interface IForexRateRepository
{
    /// <summary>Latest rate (CurrencyCode → TZS) effective at <paramref name="asOf"/>, or null if none.</summary>
    Task<decimal?> GetRateAsync(string currency, DateTime asOf, CancellationToken ct);

    /// <summary>Every currency's rate effective NOW — what /api/sap/pricing/rates serves the gate.</summary>
    Task<IReadOnlyList<ForexRateRow>> GetActiveRatesAsync(CancellationToken ct);
}

/// <summary>One currently-effective forex_rate row.</summary>
public sealed record ForexRateRow(string Currency, decimal RateToTzs, DateTime EffectiveFrom);

/// <summary>
/// Reads the manually-maintained forex_rate table in parts_catalog. Connection string is resolved
/// per-tenant via <see cref="ICompanyContext"/> (always Autohub for Phase B callers).
/// </summary>
public sealed class ForexRateRepository : IForexRateRepository
{
    private readonly ICompanyContext _company;
    public ForexRateRepository(ICompanyContext company) => _company = company;

    private string ConnectionString => _company.Current.Neon.ConnectionString;

    public async Task<decimal?> GetRateAsync(string currency, DateTime asOf, CancellationToken ct)
    {
        const string sql = """
            SELECT "RateToTzs"
            FROM forex_rate
            WHERE "CurrencyCode" = @currency
              AND "EffectiveFrom" <= @asOf
              AND ("EffectiveTo" IS NULL OR "EffectiveTo" > @asOf)
            ORDER BY "EffectiveFrom" DESC
            LIMIT 1;
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("currency", currency.ToUpperInvariant());
        cmd.Parameters.AddWithValue("asOf", asOf);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToDecimal(result);
    }

    public async Task<IReadOnlyList<ForexRateRow>> GetActiveRatesAsync(CancellationToken ct)
    {
        // One row per currency: the latest EffectiveFrom among the rows effective now.
        const string sql = """
            SELECT DISTINCT ON ("CurrencyCode") "CurrencyCode", "RateToTzs", "EffectiveFrom"
            FROM forex_rate
            WHERE "EffectiveFrom" <= NOW()
              AND ("EffectiveTo" IS NULL OR "EffectiveTo" > NOW())
            ORDER BY "CurrencyCode", "EffectiveFrom" DESC;
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var rows = new List<ForexRateRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new ForexRateRow(reader.GetString(0), reader.GetDecimal(1), reader.GetDateTime(2)));
        return rows;
    }
}
