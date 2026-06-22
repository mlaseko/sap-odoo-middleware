using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>One magnitude-based rounding band: prices in [MinPrice, MaxPrice) round to RoundTo.</summary>
public sealed record RoundingRule(decimal MinPrice, decimal? MaxPrice, int RoundTo);

public interface IPricingRoundingRuleRepository
{
    /// <summary>All rounding rules ordered by MinPrice ascending (small set; fetched per calc).</summary>
    Task<IReadOnlyList<RoundingRule>> GetRulesAsync(CancellationToken ct);
}

/// <summary>
/// Reads pricing_rounding_rules in parts_catalog — the increment-by-magnitude table used by the
/// intelligent rounding (ceiling for retail, floor for wholesale). Connection per-tenant via
/// ICompanyContext.
/// </summary>
public sealed class PricingRoundingRuleRepository : IPricingRoundingRuleRepository
{
    private readonly ICompanyContext _company;
    public PricingRoundingRuleRepository(ICompanyContext company) => _company = company;

    private string ConnectionString => _company.Current.Neon.ConnectionString;

    public async Task<IReadOnlyList<RoundingRule>> GetRulesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT "MinPrice", "MaxPrice", "RoundTo"
            FROM pricing_rounding_rules
            ORDER BY "MinPrice" ASC;
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rules = new List<RoundingRule>();
        while (await reader.ReadAsync(ct))
        {
            rules.Add(new RoundingRule(
                MinPrice: reader.GetDecimal(0),
                MaxPrice: await reader.IsDBNullAsync(1, ct) ? null : reader.GetDecimal(1),
                RoundTo: reader.GetInt32(2)));
        }
        return rules;
    }
}
