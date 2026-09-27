using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>The matched pricing_brand_ratios row: the ratio plus the band that produced it.</summary>
public sealed record RatioBand(decimal Ratio, decimal BandMin, decimal? BandMax);

public interface IPricingBrandRatioRepository
{
    /// <summary>
    /// Cost→Retail ratio (Retail = Cost ÷ ratio) for the supplier <paramref name="brand"/> and the
    /// cost band containing <paramref name="costTzs"/>, matched case-insensitively. Returns null when
    /// the brand has no active band covering that cost (caller retries with 'DEFAULT').
    /// </summary>
    Task<decimal?> GetCostToRetailRatioAsync(string brand, decimal costTzs, CancellationToken ct);

    /// <summary>
    /// Same match as <see cref="GetCostToRetailRatioAsync"/>, but returns the band bounds with the
    /// ratio — for the price preview, which shows the reviewer WHICH band actually applied.
    /// </summary>
    Task<RatioBand?> GetRatioBandAsync(string brand, decimal costTzs, CancellationToken ct);

    /// <summary>
    /// The active minimum selling price (PL03 floor, TZS, pre-rounding) for the brand from
    /// pricing_brand_floors, matched case-insensitively — or null when the brand has no
    /// floor. Deliberately NO 'DEFAULT' fallback: only brands with a measured floor get one.
    /// </summary>
    Task<decimal?> GetMinRetailFloorAsync(string brand, CancellationToken ct);
}

/// <summary>
/// Reads pricing_brand_ratios in parts_catalog. Picks the tightest active band whose
/// [BandMin, BandMax) contains the (post-markup) cost. Brand is a SUPPLIER key
/// (BORSEHUNG / DPA / OE / VIKA / DEFAULT), matched case-insensitively. Connection per-tenant via
/// ICompanyContext.
/// </summary>
public sealed class PricingBrandRatioRepository : IPricingBrandRatioRepository
{
    private readonly ICompanyContext _company;
    public PricingBrandRatioRepository(ICompanyContext company) => _company = company;

    private string ConnectionString => _company.Current.Neon.ConnectionString;

    public async Task<decimal?> GetCostToRetailRatioAsync(string brand, decimal costTzs, CancellationToken ct)
        => (await GetRatioBandAsync(brand, costTzs, ct))?.Ratio;

    public async Task<RatioBand?> GetRatioBandAsync(string brand, decimal costTzs, CancellationToken ct)
    {
        const string sql = """
            SELECT "CostToRetailRatio", "BandMin", "BandMax"
            FROM pricing_brand_ratios
            WHERE UPPER("Brand") = UPPER(@brand)
              AND "BandMin" <= @cost
              AND ("BandMax" IS NULL OR "BandMax" > @cost)
              AND "EffectiveTo" IS NULL
            ORDER BY "BandMin" DESC
            LIMIT 1;
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("brand", brand);
        cmd.Parameters.AddWithValue("cost", costTzs);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new RatioBand(
            Ratio: reader.GetDecimal(0),
            BandMin: reader.GetDecimal(1),
            BandMax: await reader.IsDBNullAsync(2, ct) ? null : reader.GetDecimal(2));
    }

    public async Task<decimal?> GetMinRetailFloorAsync(string brand, CancellationToken ct)
    {
        const string sql = """
            SELECT "MinSellPrice"
            FROM pricing_brand_floors
            WHERE UPPER("Brand") = UPPER(@brand)
              AND "EffectiveTo" IS NULL
            LIMIT 1;
            """;
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("brand", brand);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is null or DBNull ? null : Convert.ToDecimal(result);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Migration not run yet (2026-09-27__pricing_floors_and_recalc_flags.sql):
            // behave as "no floor" so item provisioning keeps working.
            return null;
        }
    }
}
