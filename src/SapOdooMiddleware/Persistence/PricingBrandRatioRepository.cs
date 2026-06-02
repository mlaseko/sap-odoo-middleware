using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

public interface IPricingBrandRatioRepository
{
    /// <summary>
    /// PL01→PL03 ratio for the brand band containing <paramref name="pl01Tzs"/>, or null if no
    /// active band matches (caller falls back to a default).
    /// </summary>
    Task<decimal?> GetRatioAsync(string brand, decimal pl01Tzs, CancellationToken ct);
}

/// <summary>
/// Reads pricing_brand_ratios in parts_catalog. Picks the tightest active band whose
/// [PriceBandMin, PriceBandMax) contains the PL01 value. Connection per-tenant via ICompanyContext.
/// </summary>
public sealed class PricingBrandRatioRepository : IPricingBrandRatioRepository
{
    private readonly ICompanyContext _company;
    public PricingBrandRatioRepository(ICompanyContext company) => _company = company;

    private string ConnectionString => _company.Current.Neon.ConnectionString;

    public async Task<decimal?> GetRatioAsync(string brand, decimal pl01Tzs, CancellationToken ct)
    {
        const string sql = """
            SELECT "Pl01ToPl03"
            FROM pricing_brand_ratios
            WHERE "Brand" = @brand
              AND "PriceBandMin" <= @pl01
              AND ("PriceBandMax" IS NULL OR "PriceBandMax" > @pl01)
              AND "EffectiveTo" IS NULL
            ORDER BY "PriceBandMin" DESC
            LIMIT 1;
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("brand", brand);
        cmd.Parameters.AddWithValue("pl01", pl01Tzs);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToDecimal(result);
    }
}
