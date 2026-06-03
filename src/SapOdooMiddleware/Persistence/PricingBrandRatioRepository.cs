using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

public interface IPricingBrandRatioRepository
{
    /// <summary>
    /// Retail→Wholesale ratio (PL05 = Retail × ratio) for the brand band containing the retail
    /// price <paramref name="retailTzs"/>, or null if no active band matches (caller uses a default).
    /// </summary>
    Task<decimal?> GetRatioAsync(string brand, decimal retailTzs, CancellationToken ct);
}

/// <summary>
/// Reads pricing_brand_ratios in parts_catalog. Picks the tightest active band whose
/// [PriceBandMin, PriceBandMax) contains the retail (selling) price. The "Pl01ToPl03" column holds
/// the retail→wholesale ratio. Connection per-tenant via ICompanyContext.
/// </summary>
public sealed class PricingBrandRatioRepository : IPricingBrandRatioRepository
{
    private readonly ICompanyContext _company;
    public PricingBrandRatioRepository(ICompanyContext company) => _company = company;

    private string ConnectionString => _company.Current.Neon.ConnectionString;

    public async Task<decimal?> GetRatioAsync(string brand, decimal retailTzs, CancellationToken ct)
    {
        const string sql = """
            SELECT "Pl01ToPl03"
            FROM pricing_brand_ratios
            WHERE "Brand" = @brand
              AND "PriceBandMin" <= @retail
              AND ("PriceBandMax" IS NULL OR "PriceBandMax" > @retail)
              AND "EffectiveTo" IS NULL
            ORDER BY "PriceBandMin" DESC
            LIMIT 1;
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("brand", brand);
        cmd.Parameters.AddWithValue("retail", retailTzs);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToDecimal(result);
    }
}
