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
/// Schema (confirmed against the live parts_catalog):
///   oitm                  — id (PK), item_code, article_number, febi_article_no, …
///   oitm_cross_reference  — oitm_id (FK → oitm.id), oem_number, reference_type ('oem' | 'iam_equivalent')
///   neon_germax_products  — item_code, germax_article_number, is_active
/// Tier 1 joins the cross-reference junction to oitm and keeps only reference_type='oem' (the 3.6M
/// 'iam_equivalent' rows are aftermarket substitutes, not the OEM, so matching on them would be wrong).
/// Tier 2 resolves the supplier article via oitm.article_number / febi_article_no (borrowed Germax
/// items keep the original supplier code in febi_article_no), falling back to neon_germax_products.
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
            SELECT o.item_code
            FROM oitm_cross_reference xref
            JOIN oitm o ON o.id = xref.oitm_id
            WHERE xref.oem_number = ANY(@oems)
              AND xref.reference_type = 'oem'
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

        // Direct article (TecDoc-direct items) first, then febi_article_no (borrowed/Germax items),
        // then neon_germax_products as a defensive fallback should febi_article_no ever lag the scraper.
        const string sql = """
            WITH article_match AS (
                SELECT item_code, 1 AS priority FROM oitm
                WHERE article_number = @article OR febi_article_no = @article
                UNION ALL
                SELECT item_code, 2 AS priority FROM neon_germax_products
                WHERE germax_article_number = @article AND is_active = true
            )
            SELECT item_code FROM article_match ORDER BY priority LIMIT 1;
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("article", articleNumber.Trim());
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (string)result;
    }
}
