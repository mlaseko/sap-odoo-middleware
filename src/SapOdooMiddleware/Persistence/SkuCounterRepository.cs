using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

public interface ISkuCounterRepository
{
    /// <summary>
    /// Atomically increments the counter for <paramref name="prefix"/> and returns the new value.
    /// Throws <see cref="InvalidOperationException"/> if the prefix has not been seeded.
    /// </summary>
    Task<long> IncrementAsync(string prefix, CancellationToken ct);
}

/// <summary>
/// Atomic per-prefix ItemCode counter in parts_catalog. Uses <c>UPDATE ... RETURNING</c> so the
/// increment-and-read is a single atomic statement (no SELECT ... FOR UPDATE race). The caller
/// formats prefix + value (e.g. "LR" + 100601 → "LR100601"). Connection per-tenant via ICompanyContext.
/// </summary>
public sealed class SkuCounterRepository : ISkuCounterRepository
{
    private readonly ICompanyContext _company;
    public SkuCounterRepository(ICompanyContext company) => _company = company;

    private string ConnectionString => _company.Current.Neon.ConnectionString;

    public async Task<long> IncrementAsync(string prefix, CancellationToken ct)
    {
        const string sql = """
            UPDATE sku_counters
            SET "CurrentValue" = "CurrentValue" + 1, "LastUpdated" = NOW()
            WHERE "Prefix" = @prefix
            RETURNING "CurrentValue";
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("prefix", prefix);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull)
            throw new InvalidOperationException(
                $"SKU prefix '{prefix}' is not seeded in sku_counters. Seed it from current SAP MAX before creating items.");
        return Convert.ToInt64(result);
    }
}
