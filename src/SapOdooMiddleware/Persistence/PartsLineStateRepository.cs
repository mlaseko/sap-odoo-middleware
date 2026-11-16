using Npgsql;
using NpgsqlTypes;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>
/// Everything needed to decide what an Autohub invoice line needs next, in one row: the line, its review
/// state, and the few enrichment facts that drive the decision (part check, marque, borrow sign-off).
/// MarqueResolved is the DGX's manufacturer_resolution.resolved ("true" / "false" / null when absent);
/// SkuPrefix is the prefix the item would be created under (blank or GEN = not resolved yet).
/// </summary>
public sealed record PartsLineState(
    Guid Id, int LineNumber, string? SupplierArticleNumber, string? Description, string? Brand,
    decimal? Quantity, decimal? UnitPriceForeign, bool IsPromotional, string ReviewStatus, string? CreateErrorMessage,
    string? EnrichmentSource, bool HasItemData, bool ConfirmationRequired, bool Confirmed,
    bool NeedsPartReview, string? PartVerdict, string? SelectedName, string? SelectedSupplier,
    string? MarqueResolved, string? MarqueResolvedPrefix, string? MarqueTopCandidate, string? SkuPrefix, int? ItemGroupCode,
    string? SuggestedDonorItemCode, string? SuggestedDonorSupplier, string? MatchedItemCode, string? GeneratedItemCode,
    string? BorrowedFromArticle, string? BorrowedFromSupplier, IReadOnlyList<string>? OemNumbers = null);

public interface IPartsLineStateRepository
{
    /// <summary>Every line of the document with its review / enrichment state, in line order.</summary>
    Task<IReadOnlyList<PartsLineState>> ListAsync(Guid documentId, CancellationToken ct);

    /// <summary>The lines of several documents in one query, keyed by document (the invoice list's progress).</summary>
    Task<IReadOnlyDictionary<Guid, List<PartsLineState>>> ListForDocumentsAsync(IReadOnlyList<Guid> documentIds, CancellationToken ct);

    /// <summary>
    /// Accept the DGX's resolved marque for the given lines only (resolved:true, with a prefix and item data,
    /// not terminal, not promotional): copy manufacturer_resolution.prefix into the SKU prefix and queue the
    /// line to create. The per-line version of "Confirm DGX-resolved marques". Returns the updated line ids.
    /// </summary>
    Task<IReadOnlyList<Guid>> ApplyResolvedMarquesAsync(Guid documentId, IReadOnlyList<Guid> lineIds, CancellationToken ct);
}

public sealed class PartsLineStateRepository : IPartsLineStateRepository
{
    private readonly ICompanyContext _company;
    public PartsLineStateRepository(ICompanyContext company) => _company = company;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_company.Current.Neon.ConnectionString);
        await c.OpenAsync(ct);
        return c;
    }

    public async Task<IReadOnlyList<PartsLineState>> ListAsync(Guid documentId, CancellationToken ct) =>
        (await QueryAsync(new[] { documentId }, ct)).GetValueOrDefault(documentId) ?? new List<PartsLineState>();

    public async Task<IReadOnlyDictionary<Guid, List<PartsLineState>>> ListForDocumentsAsync(
        IReadOnlyList<Guid> documentIds, CancellationToken ct) =>
        documentIds.Count == 0 ? new Dictionary<Guid, List<PartsLineState>>() : await QueryAsync(documentIds, ct);

    private async Task<Dictionary<Guid, List<PartsLineState>>> QueryAsync(IReadOnlyList<Guid> documentIds, CancellationToken ct)
    {
        // jsonb reads are text compares / text extracts only (shape-tolerant: an off-shape payload must not
        // fail the whole list). The group code is extracted as text and parsed in C#.
        const string sql = """
            SELECT "Id", "LineNumber", "SupplierArticleNumber", "Description", "Brand",
                   "Quantity", "UnitPriceForeign", COALESCE("IsPromotional", false), "ReviewStatus", "CreateErrorMessage",
                   "EnrichmentSource",
                   COALESCE(jsonb_typeof("EnrichmentPayloadJson"#>'{item_data}') = 'object', false) AS has_item_data,
                   COALESCE("EnrichmentConfirmationRequired", false),
                   ("EnrichmentConfirmedAt" IS NOT NULL) AS confirmed,
                   COALESCE(("EnrichmentPayloadJson"#>>'{audit,selection,needs_review}') = 'true', false) AS needs_review,
                   "EnrichmentPayloadJson"#>>'{audit,selection,selected_component_verdict}',
                   "EnrichmentPayloadJson"#>>'{audit,selection,selected_name}',
                   "EnrichmentPayloadJson"#>>'{audit,selection,selected_supplier}',
                   "EnrichmentPayloadJson"#>>'{manufacturer_resolution,resolved}',
                   "EnrichmentPayloadJson"#>>'{manufacturer_resolution,prefix}',
                   "EnrichmentPayloadJson"#>>'{manufacturer_resolution,candidates,0,code}',
                   "EnrichmentPayloadJson"#>>'{item_data,suggested_sku_prefix}',
                   "EnrichmentPayloadJson"#>>'{item_data,suggested_itms_grp_cod}',
                   "SuggestedDonorItemCode", "SuggestedDonorSupplier", "MatchedItemCode", "GeneratedItemCode",
                   "BorrowedFromArticle", "BorrowedFromSupplier", "OemNumbers"::text, "DocumentId"
            FROM public."staging_document_line"
            WHERE "DocumentId" = ANY(@docs)
            ORDER BY "DocumentId", "LineNumber", "Id";
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("docs", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = documentIds.ToArray() });
        var byDoc = new Dictionary<Guid, List<PartsLineState>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            decimal? D(int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
            var docId = r.GetGuid(30);
            if (!byDoc.TryGetValue(docId, out var list)) byDoc[docId] = list = new List<PartsLineState>();
            list.Add(new PartsLineState(
                Id: r.GetGuid(0), LineNumber: r.GetInt32(1), SupplierArticleNumber: S(2), Description: S(3), Brand: S(4),
                Quantity: D(5), UnitPriceForeign: D(6), IsPromotional: r.GetBoolean(7), ReviewStatus: S(8) ?? "pending",
                CreateErrorMessage: S(9), EnrichmentSource: S(10), HasItemData: r.GetBoolean(11),
                ConfirmationRequired: r.GetBoolean(12), Confirmed: r.GetBoolean(13),
                NeedsPartReview: r.GetBoolean(14), PartVerdict: S(15), SelectedName: S(16), SelectedSupplier: S(17),
                MarqueResolved: S(18), MarqueResolvedPrefix: S(19), MarqueTopCandidate: S(20), SkuPrefix: S(21),
                ItemGroupCode: int.TryParse(S(22), out var g) ? g : null,
                SuggestedDonorItemCode: S(23), SuggestedDonorSupplier: S(24), MatchedItemCode: S(25), GeneratedItemCode: S(26),
                BorrowedFromArticle: S(27), BorrowedFromSupplier: S(28), OemNumbers: ParseOems(S(29))));
        }
        return byDoc;
    }

    /// <summary>OemNumbers is stored as a JSON array; an unreadable value counts as none.</summary>
    private static IReadOnlyList<string> ParseOems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch (System.Text.Json.JsonException) { return Array.Empty<string>(); }
    }

    public async Task<IReadOnlyList<Guid>> ApplyResolvedMarquesAsync(
        Guid documentId, IReadOnlyList<Guid> lineIds, CancellationToken ct)
    {
        if (lineIds.Count == 0) return Array.Empty<Guid>();
        // Same update as PartsReviewRepository.BulkApplyResolvedMarquesAsync, limited to the chosen lines.
        const string sql = """
            UPDATE public."staging_document_line"
            SET "EnrichmentPayloadJson" = jsonb_set(
                    "EnrichmentPayloadJson", '{item_data,suggested_sku_prefix}',
                    "EnrichmentPayloadJson"#>'{manufacturer_resolution,prefix}', true),
                "ReviewStatus" = 'create_new',
                "CreateErrorMessage" = NULL
            WHERE "DocumentId" = @doc
              AND "Id" = ANY(@ids)
              AND "EnrichmentPayloadJson"#>>'{manufacturer_resolution,resolved}' = 'true'
              AND "EnrichmentPayloadJson"#>'{item_data}' IS NOT NULL
              AND "EnrichmentPayloadJson"#>>'{manufacturer_resolution,prefix}' IS NOT NULL
              AND "ReviewStatus" NOT IN ('created','matched','skip')
              AND "IsPromotional" = false
            RETURNING "Id";
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = lineIds.ToArray() });
        var updated = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) updated.Add(r.GetGuid(0));
        return updated;
    }
}
