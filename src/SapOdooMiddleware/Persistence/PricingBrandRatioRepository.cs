using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

public interface IPricingBrandRatioRepository
{
    /// <summary>
    /// Cost→Retail ratio (Retail = Cost ÷ ratio) for the supplier <paramref name="brand"/> and the
    /// cost band containing <paramref name="costTzs"/>, matched case-insensitively. Returns null when
    /// the brand has no active band covering that cost (caller retries with 'DEFAULT').
    /// </summary>
    Task<decimal?> GetCostToRetailRatioAsync(string brand, decimal costTzs, CancellationToken ct);
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
    {
        const string sql = """
            SELECT "CostToRetailRatio"
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
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToDecimal(result);
    }
}
