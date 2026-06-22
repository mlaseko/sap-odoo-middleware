using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>A candidate SAP item found by auto-match, with the supplier identity needed to gate the match.</summary>
public sealed record OitmMatch(
    string ItemCode,
    long? OitmId,
    string? SupplierName,
    string MatchedField);   // "cross_ref_oem" | "article_number" | "febi_article_no" | "germax_article_number"

public interface IOitmMatchRepository
{
    /// <summary>Tier 1: the SAP item whose OEM cross-reference matches any of the given OEMs (with supplier), or null.</summary>
    Task<OitmMatch?> FindByOemAsync(IReadOnlyList<string> oemNumbers, CancellationToken ct);

    /// <summary>
    /// Tier 2: the SAP item whose supplier article number equals <paramref name="articleNumber"/> (with
    /// supplier), or null. <paramref name="searchingSupplier"/> is the invoice line's supplier identity:
    /// the <c>febi_article_no</c> column is ONLY consulted when that supplier is explicitly FEBI, so a
    /// non-FEBI article that happens to equal some FEBI item's febi_article_no can't bleed into a match.
    /// </summary>
    Task<OitmMatch?> FindByArticleAsync(string articleNumber, string? searchingSupplier, CancellationToken ct);
}

/// <summary>
/// Reads the parts_catalog mirror of SAP items for auto-matching (D2). Connection per-tenant via
/// ICompanyContext. Returns supplier identity alongside the item code so callers can enforce the
/// one-SAP-item-per-supplier rule (Slice 1.6).
///
/// Schema (confirmed against the live parts_catalog):
///   oitm                  — id (PK), item_code, article_number, febi_article_no, supplier_name, …
///   oitm_cross_reference  — oitm_id (FK → oitm.id), oem_number, reference_type ('oem' | 'iam_equivalent')
///   neon_germax_products  — item_code, germax_article_number, is_active
/// Tier 1 joins the cross-reference junction to oitm and keeps only reference_type='oem' (the 3.6M
/// 'iam_equivalent' rows are aftermarket substitutes, not the OEM). Both tiers only return rows that
/// actually carry a SAP item_code.
/// </summary>
public sealed class OitmMatchRepository : IOitmMatchRepository
{
    private readonly ICompanyContext _company;
    public OitmMatchRepository(ICompanyContext company) => _company = company;

    private string ConnectionString => _company.Current.Neon.ConnectionString;

    public async Task<OitmMatch?> FindByOemAsync(IReadOnlyList<string> oemNumbers, CancellationToken ct)
    {
        if (oemNumbers is null || oemNumbers.Count == 0) return null;

        const string sql = """
            SELECT o.item_code, o.id AS oitm_id, o.supplier_name, 'cross_ref_oem' AS matched_field
            FROM oitm_cross_reference xref
            JOIN oitm o ON o.id = xref.oitm_id
            WHERE xref.oem_number = ANY(@oems)
              AND xref.reference_type = 'oem'
              AND o.item_code IS NOT NULL
            LIMIT 1;
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("oems", oemNumbers.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task<OitmMatch?> FindByArticleAsync(string articleNumber, string? searchingSupplier, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(articleNumber)) return null;

        // FEBI guard: febi_article_no is FEBI's own numbering. Matching it for a NON-FEBI supplier lets a
        // coincidental article collision link to the wrong supplier's item, so only consult that column
        // when the searching supplier is explicitly FEBI. (Defensive — under audit.) Direct article first,
        // then (FEBI only) febi_article_no, then neon_germax_products as a scraper-lag fallback.
        var includeFebi = IsFebi(searchingSupplier);
        const string sql = """
            WITH article_match AS (
                SELECT o.item_code, o.id AS oitm_id, o.supplier_name,
                       CASE WHEN o.article_number = @article THEN 'article_number' ELSE 'febi_article_no' END AS matched_field,
                       1 AS priority
                FROM oitm o
                WHERE (o.article_number = @article OR (@includeFebi AND o.febi_article_no = @article))
                  AND o.item_code IS NOT NULL
                UNION ALL
                SELECT g.item_code, NULL::bigint, NULL::text, 'germax_article_number' AS matched_field, 2 AS priority
                FROM neon_germax_products g
                WHERE g.germax_article_number = @article AND g.is_active = true
            )
            SELECT item_code, oitm_id, supplier_name, matched_field
            FROM article_match ORDER BY priority LIMIT 1;
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("article", articleNumber.Trim());
        cmd.Parameters.AddWithValue("includeFebi", includeFebi);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    /// <summary>The searching supplier is FEBI (covers "FEBI" and "febi bilstein").</summary>
    private static bool IsFebi(string? supplier) =>
        !string.IsNullOrWhiteSpace(supplier) && supplier.Contains("FEBI", StringComparison.OrdinalIgnoreCase);

    private static OitmMatch Map(NpgsqlDataReader r) => new(
        ItemCode:     r.GetString(0),
        OitmId:       r.IsDBNull(1) ? null : r.GetInt64(1),
        SupplierName: r.IsDBNull(2) ? null : r.GetString(2),
        MatchedField: r.GetString(3));
}
