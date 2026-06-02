using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

public interface IOitmMatchRepository
{
    /// <summary>Tier 1: ItemCode whose OEM cross-reference matches any of the given OEMs, or null.</summary>
    Task<string?> FindItemCodeByOemAsync(IReadOnlyList<string> oemNumbers, CancellationToken ct);

    /// <summary>Tier 2: ItemCode whose supplier article number equals <paramref name="articleNumber"/>, or null.</summary>
    Task<string?> FindItemCodeByArticleAsync(string articleNumber, CancellationToken ct);
}

/// <summary>
/// Reads the parts_catalog mirror of SAP items for auto-matching (D2). Connection per-tenant via
/// ICompanyContext.
///
/// NOTE: the table/column names below follow the Phase B spec (§6.1) — oitm_cross_reference
/// (oem_number, item_code) and oitm (item_code, "U_Article_No"). These are assumptions about the
/// parts_catalog schema that could not be verified in this environment; adjust the identifiers
/// here if the real mirror differs. Everything else keys off these two methods.
/// </summary>
public sealed class OitmMatchRepository : IOitmMatchRepository
{
    private readonly ICompanyContext _company;
    public OitmMatchRepository(ICompanyContext company) => _company = company;

    private string ConnectionString => _company.Current.Neon.ConnectionString;

    public async Task<string?> FindItemCodeByOemAsync(IReadOnlyList<string> oemNumbers, CancellationToken ct)
    {
        if (oemNumbers is null || oemNumbers.Count == 0) return null;

        const string sql = """
            SELECT item_code
            FROM oitm_cross_reference
            WHERE oem_number = ANY(@oems)
            LIMIT 1;
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("oems", oemNumbers.ToArray());
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (string)result;
    }

    public async Task<string?> FindItemCodeByArticleAsync(string articleNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(articleNumber)) return null;

        const string sql = """
            SELECT item_code
            FROM oitm
            WHERE "U_Article_No" = @article
            LIMIT 1;
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("article", articleNumber.Trim());
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (string)result;
    }
}
