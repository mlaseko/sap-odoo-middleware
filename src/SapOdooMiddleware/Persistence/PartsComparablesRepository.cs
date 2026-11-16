using Npgsql;
using NpgsqlTypes;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>
/// What a staging line is linked to in parts_catalog: the oitm rows it was enriched from, matched to
/// or created as (its anchors), its own SAP item codes (never shown as its own related part), and the
/// part name (oitm.part_component of the first anchor that has one).
/// </summary>
public sealed record PartsLineAnchors(
    Guid LineId, IReadOnlyList<long> AnchorIds, IReadOnlyList<string> OwnItemCodes, string? PartComponent);

/// <summary>
/// An item active in SAP that shares a genuine OEM number with a line, or is recorded as its related
/// part, with its catalogue details from Neon (oitm + the NeonAutoHubProducts mirror of SAP).
/// Brand is the selling brand (U_MdlTEST); OnHand / Available are the mirror's stock figures.
/// Only the prices come from SAP, separately.
/// </summary>
public sealed record PartsComparableCandidate(
    long OitmId, string ItemCode, string? Name, string? ItemName, string? PartComponent, string? PartGroup,
    string? Brand, string? ArticleNumber, string? SupplierName, string? ImageUrl, int VehiclesCount,
    decimal OnHand, decimal Available,
    IReadOnlyList<string> SharedOems, IReadOnlyList<string> Via);

public interface IPartsComparablesRepository
{
    /// <summary>The line's anchors, own item codes and part name. Null when the line is not on the document.</summary>
    Task<PartsLineAnchors?> GetLineAnchorsAsync(Guid documentId, Guid lineId, CancellationToken ct);

    /// <summary>
    /// Every SAP-active item linked to the anchors or OEM tokens by a shared genuine OEM number or an
    /// oitm_related_parts row, most shared OEMs first. No part-name or tier filter here.
    /// </summary>
    Task<IReadOnlyList<PartsComparableCandidate>> FindCandidatesAsync(
        IReadOnlyList<long> anchorIds, IReadOnlyList<string> oemTokens, IReadOnlyList<string> excludeItemCodes,
        int limit, CancellationToken ct);

    /// <summary>parts_brand_tier: brand_key (upper-case, single spaces) → tier.</summary>
    Task<IReadOnlyDictionary<string, string>> GetBrandTiersAsync(CancellationToken ct);
}

/// <summary>
/// Related-part lookups on parts_catalog, ported from the DGX price gate (price_gate/comparables.py).
/// OEM sources, genuine only: oitm_cross_reference (reference_type 'oem', never IAM equivalents),
/// oitm_extracted_oems (classification 'oem') and oitm.canonical_oem_number, plus oitm_related_parts.
/// Guards: an OEM shorter than 5 characters, or shared by more than <see cref="OemCollisionMax"/>
/// items, is too generic to compare on; a token equal to an internal item code (an LR-shaped Molas
/// SKU) is never used as an OEM. Only items active in NeonAutoHubProducts are returned.
/// </summary>
public sealed class PartsComparablesRepository : IPartsComparablesRepository
{
    /// <summary>An OEM shared by more items than this is too generic to compare on.</summary>
    public const int OemCollisionMax = 40;

    private readonly ICompanyContext _company;
    public PartsComparablesRepository(ICompanyContext company) => _company = company;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_company.Current.Neon.ConnectionString);
        await c.OpenAsync(ct);
        return c;
    }

    public async Task<PartsLineAnchors?> GetLineAnchorsAsync(Guid documentId, Guid lineId, CancellationToken ct)
    {
        // Anchors: the enrichment row, the confirmed donor, the matched row, plus any oitm row carrying
        // one of the line's own item codes. Part name from the first anchor (in that order) that has one.
        const string sql = """
            WITH l AS (
                SELECT "NeonOitmId", "EnrichmentOitmId", "SuggestedDonorOitmId", "MatchedOitmId",
                       "GeneratedItemCode", "MatchedItemCode"
                FROM public."staging_document_line"
                WHERE "Id" = @line AND "DocumentId" = @doc
            ),
            direct AS (
                SELECT id::bigint, ord FROM l,
                    LATERAL (VALUES (l."NeonOitmId"::bigint, 1), (l."EnrichmentOitmId"::bigint, 2),
                                    (l."SuggestedDonorOitmId"::bigint, 3), (l."MatchedOitmId"::bigint, 4)) v(id, ord)
                WHERE id IS NOT NULL
            ),
            by_code AS (
                SELECT o.id::bigint, 5 AS ord FROM l
                JOIN public.oitm o ON o.item_code IN (l."GeneratedItemCode", l."MatchedItemCode")
            ),
            anchors AS (
                SELECT id, min(ord) AS ord FROM (SELECT * FROM direct UNION ALL SELECT * FROM by_code) a GROUP BY id
            )
            SELECT (SELECT count(*) FROM l) AS found,
                   COALESCE((SELECT array_agg(id ORDER BY ord, id) FROM anchors), '{}'::bigint[]) AS anchor_ids,
                   (SELECT array_remove(ARRAY["GeneratedItemCode", "MatchedItemCode"], NULL) FROM l) AS own_codes,
                   (SELECT o.part_component FROM anchors a JOIN public.oitm o ON o.id = a.id
                     WHERE COALESCE(btrim(o.part_component), '') <> ''
                     ORDER BY a.ord, a.id LIMIT 1) AS part_component;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        cmd.Parameters.AddWithValue("line", lineId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct) || r.GetInt64(0) == 0) return null;

        var anchors = r.IsDBNull(1) ? Array.Empty<long>() : r.GetFieldValue<long[]>(1);
        var own = r.IsDBNull(2) ? Array.Empty<string>() : r.GetFieldValue<string[]>(2);
        return new PartsLineAnchors(lineId, anchors, own, r.IsDBNull(3) ? null : r.GetString(3));
    }

    // Same normalisation as the index idx_oitm_cr_oem_number_norm_pattern, so the xref join is indexed.
    private static string Norm(string expr) => $"upper(regexp_replace({expr}::text, '[\\s./\\-]', '', 'g'))";

    private static readonly string CandidatesSql = $"""
        WITH anchor_oems AS (
            SELECT {Norm("x.oem_number")} AS n FROM public.oitm_cross_reference x
             WHERE x.oitm_id = ANY(@anchors) AND x.reference_type = 'oem'
            UNION SELECT {Norm("e.raw_token")} FROM public.oitm_extracted_oems e
             WHERE e.oitm_id = ANY(@anchors) AND e.classification = 'oem'
            UNION SELECT {Norm("o.canonical_oem_number")} FROM public.oitm o
             WHERE o.id = ANY(@anchors) AND COALESCE(o.canonical_oem_number, '') <> ''
            UNION SELECT unnest(@toks::text[])
        ),
        oems AS (
            SELECT n FROM anchor_oems
             WHERE length(n) >= 5
               AND n NOT IN (SELECT upper("ItemCode") FROM public."NeonAutoHubProducts" WHERE "ItemCode" IS NOT NULL)
               AND n NOT IN (SELECT upper(item_code) FROM public.oitm WHERE item_code IS NOT NULL)
        ),
        hits AS (
            SELECT x.oitm_id, o.n, 'oem_xref'::text AS via
              FROM public.oitm_cross_reference x JOIN oems o ON {Norm("x.oem_number")} = o.n
             WHERE x.reference_type = 'oem'
            UNION ALL
            SELECT e.oitm_id, o.n, 'extracted_oem'
              FROM public.oitm_extracted_oems e JOIN oems o ON e.normalized = o.n
             WHERE e.classification = 'oem'
            UNION ALL
            SELECT i.id, o.n, 'canonical'
              FROM public.oitm i JOIN oems o ON {Norm("i.canonical_oem_number")} = o.n
        ),
        generic AS (
            SELECT n FROM hits GROUP BY n HAVING count(DISTINCT oitm_id) > @cmax
        ),
        all_hits AS (
            SELECT oitm_id, n, via FROM hits WHERE n NOT IN (SELECT n FROM generic)
            UNION ALL
            SELECT r.related_oitm_id, r.shared_oem, 'related_parts'
              FROM public.oitm_related_parts r WHERE r.oitm_id = ANY(@anchors)
            UNION ALL
            SELECT r.oitm_id, r.shared_oem, 'related_parts'
              FROM public.oitm_related_parts r WHERE r.related_oitm_id = ANY(@anchors)
        )
        SELECT i.id::bigint, i.item_code, i.name, NULLIF(TRIM(p."U_Item_Name"), '') AS item_name,
               i.part_component, i.part_group, NULLIF(TRIM(p."U_MdlTEST"), '') AS brand,
               COALESCE(NULLIF(TRIM(i.article_number), ''), NULLIF(TRIM(p."U_Article_No"), '')) AS article,
               i.supplier_name, i.image_url, COALESCE(i.compatible_vehicles_count, 0)::int AS vehicles,
               COALESCE(p."OnHandSap", 0)::numeric AS on_hand, COALESCE(p."AvailableCache", 0)::numeric AS available,
               array_remove(array_agg(DISTINCT h.n), NULL) AS shared_oems,
               array_agg(DISTINCT h.via) AS via
          FROM all_hits h
          JOIN public.oitm i ON i.id = h.oitm_id
          JOIN public."NeonAutoHubProducts" p ON p."ItemCode" = i.item_code AND p."IsActive"
         WHERE NOT (upper(i.item_code) = ANY(@exclude))
         GROUP BY 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13
         ORDER BY count(DISTINCT h.n) DESC, i.item_code
         LIMIT @lim;
        """;

    public async Task<IReadOnlyList<PartsComparableCandidate>> FindCandidatesAsync(
        IReadOnlyList<long> anchorIds, IReadOnlyList<string> oemTokens, IReadOnlyList<string> excludeItemCodes,
        int limit, CancellationToken ct)
    {
        if (anchorIds.Count == 0 && oemTokens.Count == 0)
            return Array.Empty<PartsComparableCandidate>();

        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(CandidatesSql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("anchors", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = anchorIds.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("toks", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = oemTokens.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("exclude", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = excludeItemCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim().ToUpperInvariant()).ToArray(),
        });
        cmd.Parameters.AddWithValue("cmax", OemCollisionMax);
        cmd.Parameters.AddWithValue("lim", limit);

        var list = new List<PartsComparableCandidate>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            list.Add(new PartsComparableCandidate(
                OitmId: r.GetInt64(0),
                ItemCode: r.GetString(1),
                Name: Str(2),
                ItemName: Str(3),
                PartComponent: Str(4),
                PartGroup: Str(5),
                Brand: Str(6),
                ArticleNumber: Str(7),
                SupplierName: Str(8),
                ImageUrl: Str(9),
                VehiclesCount: r.GetInt32(10),
                OnHand: r.GetDecimal(11),
                Available: r.GetDecimal(12),
                SharedOems: r.IsDBNull(13) ? Array.Empty<string>() : r.GetFieldValue<string[]>(13),
                Via: r.IsDBNull(14) ? Array.Empty<string>() : r.GetFieldValue<string[]>(14)));
        }
        return list;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetBrandTiersAsync(CancellationToken ct)
    {
        const string sql = "SELECT brand_key, tier FROM public.parts_brand_tier;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            map[r.GetString(0)] = r.GetString(1);
        return map;
    }
}
