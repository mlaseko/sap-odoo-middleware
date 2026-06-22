using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

public interface IForexRateRepository
{
    /// <summary>Latest rate (CurrencyCode → TZS) effective at <paramref name="asOf"/>, or null if none.</summary>
    Task<decimal?> GetRateAsync(string currency, DateTime asOf, CancellationToken ct);
}

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
}
