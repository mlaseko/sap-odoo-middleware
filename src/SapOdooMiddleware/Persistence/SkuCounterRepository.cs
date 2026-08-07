using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

public interface ISkuCounterRepository
{
    /// <summary>
    /// Atomically increments the counter for <paramref name="prefix"/> and returns the new value.
    /// Throws <see cref="InvalidOperationException"/> if the prefix has not been seeded, or
    /// <see cref="SkuCounterExhaustedException"/> if it has reached its <c>MaxAllowed</c> ceiling.
    /// </summary>
    Task<long> IncrementAsync(string prefix, CancellationToken ct);
}

/// <summary>
/// A SKU counter has reached its <c>MaxAllowed</c> ceiling — no more codes can be minted under this
/// prefix until an operator extends the range. A DISTINCT type (not <see cref="InvalidOperationException"/>)
/// so callers hold the line for a decision instead of silently falling back or auto-seeding — silent
/// fallback on an unhandled counter state is exactly what produced the GEN duplication.
/// </summary>
public sealed class SkuCounterExhaustedException : Exception
{
    public string Prefix { get; }
    public long CurrentValue { get; }
    public long? MaxAllowed { get; }

    public SkuCounterExhaustedException(string prefix, long currentValue, long? maxAllowed)
        : base($"SKU prefix '{prefix}' has reached its ceiling ({currentValue}/{(maxAllowed?.ToString() ?? "∞")}). " +
               "Extend MaxAllowed for this prefix in sku_counters before creating more items.")
    {
        Prefix = prefix;
        CurrentValue = currentValue;
        MaxAllowed = maxAllowed;
    }
}

/// <summary>
/// Atomic per-prefix ItemCode counter in parts_catalog. Uses <c>UPDATE ... RETURNING</c> so the
/// increment-and-read is a single atomic statement (no SELECT ... FOR UPDATE race). The caller
/// formats prefix + value (e.g. "LR" + 100601 → "LR100601"). Connection per-tenant via ICompanyContext.
///
/// Neon is the hot allocation path; these counters are NOT manually seeded — SapSkuCounterRefreshService
/// bumps them up to the live SAP MAX (max(neon, sap), never backwards; capped at the per-prefix
/// "MaxAllowed" ceiling that excludes parked test items) on startup, daily, and on demand via
/// POST /api/admin/sku-counters/refresh.
/// </summary>
public sealed class SkuCounterRepository : ISkuCounterRepository
{
    private readonly ICompanyContext _company;
    public SkuCounterRepository(ICompanyContext company) => _company = company;

    private string ConnectionString => _company.Current.Neon.ConnectionString;

    public async Task<long> IncrementAsync(string prefix, CancellationToken ct)
    {
        // Only bump while below the ceiling (a NULL MaxAllowed means uncapped). Enforcing the cap here — in
        // the same atomic statement as the increment — is what stops a counter silently overrunning its
        // range; MaxAllowed was previously read only by the refresh path and ignored on the hot mint path.
        const string sql = """
            UPDATE sku_counters
            SET "CurrentValue" = "CurrentValue" + 1, "LastUpdated" = NOW()
            WHERE "Prefix" = @prefix AND ("MaxAllowed" IS NULL OR "CurrentValue" < "MaxAllowed")
            RETURNING "CurrentValue";
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("prefix", prefix);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is not (null or DBNull))
                return Convert.ToInt64(result);
        }

        // No row updated: the prefix is either NOT seeded or AT/OVER its ceiling. Probe to tell them apart
        // so callers can react differently — seed-and-retry for the former, hold-for-a-new-range for the
        // latter. Both must be explicit; neither may silently fall back to a generic counter.
        const string probe = """SELECT "CurrentValue", "MaxAllowed" FROM sku_counters WHERE "Prefix" = @prefix;""";
        await using (var cmd = new NpgsqlCommand(probe, conn))
        {
            cmd.Parameters.AddWithValue("prefix", prefix);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                throw new InvalidOperationException(
                    $"SKU prefix '{prefix}' is not seeded in sku_counters. Seed it from current SAP MAX before creating items.");

            var current = Convert.ToInt64(r.GetValue(0));
            var max = r.IsDBNull(1) ? (long?)null : Convert.ToInt64(r.GetValue(1));
            throw new SkuCounterExhaustedException(prefix, current, max);
        }
    }
}
