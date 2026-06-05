using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>Rich staging line for the Phase B review UI (base extraction fields + review/audit state).</summary>
public sealed record PartsReviewLineRow(
    Guid Id, Guid DocumentId, int LineNumber, int? PageNumber,
    string? SupplierArticleNumber, List<string> OemNumbers, string? Description, string? Brand,
    decimal? Quantity, string? Unit, decimal? UnitPriceForeign, decimal? DiscountPct, decimal? LineTotalForeign,
    bool IsPromotional, string ReviewStatus, string? MatchedItemCode, string? GeneratedItemCode,
    string? EnrichmentSource, string? BorrowedFromArticle, DateTime? EnrichmentConfirmedAt, string? CreateErrorMessage,
    string? MatchStrategy, string? BorrowedFromSupplier);

/// <summary>A 'create_new' line reduced to what provisioning needs (incl. the persisted enrichment).</summary>
public sealed record PartsProvisioningLine(
    Guid Id, string? SupplierArticleNumber, List<string> OemNumbers, string? Brand,
    string? Description, decimal? UnitPriceForeign, bool EnrichmentConfirmed,
    long? NeonOitmId, string? EnrichmentPayloadJson);

/// <summary>A line awaiting background enrichment.</summary>
public sealed record EnrichmentCandidate(
    Guid Id, Guid DocumentId, string? SupplierArticleNumber, List<string> OemNumbers, string? Brand, string? Description);

public interface IPartsReviewRepository
{
    Task<IReadOnlyList<PartsReviewLineRow>> ListByDocumentAsync(Guid documentId, CancellationToken ct);
    Task<PartsReviewLineRow?> GetByIdAsync(Guid lineId, CancellationToken ct);
    Task SetReviewStatusAsync(Guid lineId, string status, string? matchedItemCode, CancellationToken ct);
    Task<int> BulkSetPendingToCreateNewAsync(Guid documentId, CancellationToken ct);

    /// <summary>Skip every unresolved (pending / needs_manual) line; sets MatchStrategy='skipped'. Returns the count affected.</summary>
    Task<int> BulkSkipPendingAsync(Guid documentId, CancellationToken ct);

    /// <summary>Undo bulk-skip: move every skipped line back to 'needs_manual' (clears MatchStrategy). Returns the count.</summary>
    Task<int> BulkReopenSkippedAsync(Guid documentId, CancellationToken ct);

    /// <summary>Operator edits to an extracted line before creation (qty / unit price / description). Recomputes the line total.</summary>
    Task UpdateLineFieldsAsync(Guid lineId, decimal? quantity, decimal? unitPriceForeign, string? description, CancellationToken ct);

    /// <summary>The persisted DGX enrichment JSON for a line (for the detail panel), or null if never enriched.</summary>
    Task<string?> GetEnrichmentPayloadAsync(Guid lineId, CancellationToken ct);
    Task<Dictionary<string, int>> GetStatusCountsAsync(Guid documentId, CancellationToken ct);
    Task SetEnrichmentAsync(Guid lineId, string? source, string? borrowedArticle, string? borrowedSupplier, string? confirmedBy, CancellationToken ct);

    /// <summary>Persist the full enrichment outcome (source, borrowed, neon_oitm_id, status, strategy, payload) on the line.</summary>
    Task RecordEnrichmentResultAsync(Guid lineId, string? source, string? borrowedArticle, string? borrowedSupplier,
        long? neonOitmId, bool confirmationRequired, string status, string? errorCode, string? matchStrategy,
        string? payloadJson, CancellationToken ct);

    Task ConfirmEnrichmentAsync(Guid lineId, string confirmedBy, CancellationToken ct);
    Task RecordCreatedAsync(Guid lineId, string itemCode, decimal pl01, decimal pl03, decimal pl05, decimal forexRate, CancellationToken ct);
    Task RecordCreateFailedAsync(Guid lineId, string error, CancellationToken ct);
    Task<IReadOnlyList<PartsProvisioningLine>> ListCreateNewAsync(Guid documentId, CancellationToken ct);

    /// <summary>Pending, non-promotional, not-yet-enriched lines on extracted documents (background worker).</summary>
    Task<IReadOnlyList<EnrichmentCandidate>> GetLinesNeedingEnrichmentAsync(int limit, CancellationToken ct);
}

/// <summary>
/// Phase B review + provisioning operations on parts_catalog.staging_document_line. Separate from
/// the Phase A repo (extraction) and the slice-2 match repo (worker). Connection per-tenant via
/// ICompanyContext.
/// </summary>
public sealed class PartsReviewRepository : IPartsReviewRepository
{
    private const string Cols =
        "\"Id\",\"DocumentId\",\"LineNumber\",\"PageNumber\",\"SupplierArticleNumber\",\"OemNumbers\"," +
        "\"Description\",\"Brand\",\"Quantity\",\"Unit\",\"UnitPriceForeign\",\"DiscountPct\"," +
        "\"LineTotalForeign\",\"IsPromotional\",\"ReviewStatus\",\"MatchedItemCode\",\"GeneratedItemCode\"," +
        "\"EnrichmentSource\",\"BorrowedFromArticle\",\"EnrichmentConfirmedAt\",\"CreateErrorMessage\"," +
        "\"MatchStrategy\",\"BorrowedFromSupplier\"";

    private readonly ICompanyContext _company;
    public PartsReviewRepository(ICompanyContext company) => _company = company;
    private string ConnectionString => _company.Current.Neon.ConnectionString;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync(ct);
        return c;
    }

    public async Task<IReadOnlyList<PartsReviewLineRow>> ListByDocumentAsync(Guid documentId, CancellationToken ct)
    {
        var sql = $"SELECT {Cols} FROM public.\"staging_document_line\" WHERE \"DocumentId\" = @doc ORDER BY \"LineNumber\";";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        var list = new List<PartsReviewLineRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task<PartsReviewLineRow?> GetByIdAsync(Guid lineId, CancellationToken ct)
    {
        var sql = $"SELECT {Cols} FROM public.\"staging_document_line\" WHERE \"Id\" = @id;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", lineId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task SetReviewStatusAsync(Guid lineId, string status, string? matchedItemCode, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document_line"
            SET "ReviewStatus" = @status,
                "MatchedItemCode" = COALESCE(@code, "MatchedItemCode")
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("code", (object?)matchedItemCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", lineId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> BulkSetPendingToCreateNewAsync(Guid documentId, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document_line"
            SET "ReviewStatus" = 'create_new'
            WHERE "DocumentId" = @doc AND "ReviewStatus" = 'pending' AND "IsPromotional" = false;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> BulkSkipPendingAsync(Guid documentId, CancellationToken ct)
    {
        // Idempotent: a second call finds nothing pending/needs_manual and affects 0 rows. Already-matched
        // / created lines are untouched. Uses the existing 'skip' review status (terminal for completion).
        const string sql = """
            UPDATE public."staging_document_line"
            SET "ReviewStatus" = 'skip', "MatchStrategy" = 'skipped'
            WHERE "DocumentId" = @doc AND "ReviewStatus" IN ('pending', 'needs_manual');
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> BulkReopenSkippedAsync(Guid documentId, CancellationToken ct)
    {
        // Inverse of BulkSkipPendingAsync: skipped → needs_manual so the operator can re-decide. Idempotent.
        const string sql = """
            UPDATE public."staging_document_line"
            SET "ReviewStatus" = 'needs_manual', "MatchStrategy" = NULL
            WHERE "DocumentId" = @doc AND "ReviewStatus" = 'skip';
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateLineFieldsAsync(Guid lineId, decimal? quantity, decimal? unitPriceForeign, string? description, CancellationToken ct)
    {
        // COALESCE keeps untouched fields; the line total is kept consistent with the edited qty/price.
        const string sql = """
            UPDATE public."staging_document_line"
            SET "Quantity" = COALESCE(@qty, "Quantity"),
                "UnitPriceForeign" = COALESCE(@up, "UnitPriceForeign"),
                "Description" = COALESCE(@desc, "Description"),
                "LineTotalForeign" = ROUND(
                    COALESCE(@qty, "Quantity") * COALESCE(@up, "UnitPriceForeign")
                    * (1 - COALESCE("DiscountPct", 0) / 100.0), 2)
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("qty", (object?)quantity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("up", (object?)unitPriceForeign ?? DBNull.Value);
        cmd.Parameters.AddWithValue("desc", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", lineId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetEnrichmentPayloadAsync(Guid lineId, CancellationToken ct)
    {
        const string sql = "SELECT \"EnrichmentPayloadJson\" FROM public.\"staging_document_line\" WHERE \"Id\" = @id;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", lineId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (string)result;
    }

    public async Task<Dictionary<string, int>> GetStatusCountsAsync(Guid documentId, CancellationToken ct)
    {
        const string sql = """
            SELECT "ReviewStatus", COUNT(*) FROM public."staging_document_line"
            WHERE "DocumentId" = @doc GROUP BY "ReviewStatus";
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        var counts = new Dictionary<string, int>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) counts[r.GetString(0)] = (int)r.GetInt64(1);
        return counts;
    }

    public async Task SetEnrichmentAsync(Guid lineId, string? source, string? borrowedArticle, string? borrowedSupplier, string? confirmedBy, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document_line"
            SET "EnrichmentSource" = @source,
                "BorrowedFromArticle" = @ba,
                "BorrowedFromSupplier" = @bs,
                "EnrichmentConfirmedBy" = @by,
                "EnrichmentConfirmedAt" = CASE WHEN @by IS NULL THEN NULL ELSE NOW() END
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("source", (object?)source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ba", (object?)borrowedArticle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("bs", (object?)borrowedSupplier ?? DBNull.Value);
        cmd.Parameters.AddWithValue("by", (object?)confirmedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", lineId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordEnrichmentResultAsync(Guid lineId, string? source, string? borrowedArticle, string? borrowedSupplier,
        long? neonOitmId, bool confirmationRequired, string status, string? errorCode, string? matchStrategy,
        string? payloadJson, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document_line"
            SET "EnrichmentSource" = @source,
                "BorrowedFromArticle" = @ba,
                "BorrowedFromSupplier" = @bs,
                "NeonOitmId" = @oitm,
                "EnrichmentConfirmationRequired" = @confreq,
                "EnrichmentStatus" = @status,
                "EnrichmentErrorCode" = @err,
                "MatchStrategy" = @ms,
                "EnrichedAt" = NOW(),
                "EnrichmentPayloadJson" = @payload
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("source", (object?)source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ba", (object?)borrowedArticle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("bs", (object?)borrowedSupplier ?? DBNull.Value);
        cmd.Parameters.AddWithValue("oitm", (object?)neonOitmId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("confreq", confirmationRequired);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("err", (object?)errorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ms", (object?)matchStrategy ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
        {
            Value = (object?)payloadJson ?? DBNull.Value
        });
        cmd.Parameters.AddWithValue("id", lineId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<EnrichmentCandidate>> GetLinesNeedingEnrichmentAsync(int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT l."Id", l."DocumentId", l."SupplierArticleNumber", l."OemNumbers", l."Brand", l."Description"
            FROM public."staging_document_line" l
            JOIN public."staging_document" d ON d."Id" = l."DocumentId"
            WHERE d."Status" = 'extracted'
              AND l."ReviewStatus" = 'pending'
              AND l."EnrichmentSource" IS NULL
              AND l."IsPromotional" = false
            ORDER BY l."DocumentId", l."LineNumber"
            LIMIT @limit;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);
        var list = new List<EnrichmentCandidate>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new EnrichmentCandidate(
                Id:                    r.GetGuid(0),
                DocumentId:            r.GetGuid(1),
                SupplierArticleNumber: r.IsDBNull(2) ? null : r.GetString(2),
                OemNumbers:            ParseOems(r, 3),
                Brand:                 r.IsDBNull(4) ? null : r.GetString(4),
                Description:           r.IsDBNull(5) ? null : r.GetString(5)));
        }
        return list;
    }

    public async Task ConfirmEnrichmentAsync(Guid lineId, string confirmedBy, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document_line"
            SET "EnrichmentConfirmedBy" = @by, "EnrichmentConfirmedAt" = NOW()
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("by", confirmedBy);
        cmd.Parameters.AddWithValue("id", lineId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordCreatedAsync(Guid lineId, string itemCode, decimal pl01, decimal pl03, decimal pl05, decimal forexRate, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document_line"
            SET "ReviewStatus" = 'created', "GeneratedItemCode" = @code, "CreatedSku" = @code,
                "MatchedItemCode" = @code, "Pl01Tzs" = @pl01, "Pl03Tzs" = @pl03, "Pl05Tzs" = @pl05,
                "ForexRateUsed" = @rate, "WrittenToSapAt" = NOW(), "CreatedAt" = NOW(),
                "CreateErrorMessage" = NULL
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("code", itemCode);
        cmd.Parameters.AddWithValue("pl01", pl01);
        cmd.Parameters.AddWithValue("pl03", pl03);
        cmd.Parameters.AddWithValue("pl05", pl05);
        cmd.Parameters.AddWithValue("rate", forexRate);
        cmd.Parameters.AddWithValue("id", lineId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordCreateFailedAsync(Guid lineId, string error, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document_line"
            SET "ReviewStatus" = 'create_failed', "CreateErrorMessage" = @err
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("err", error.Length > 1000 ? error[..1000] : error);
        cmd.Parameters.AddWithValue("id", lineId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PartsProvisioningLine>> ListCreateNewAsync(Guid documentId, CancellationToken ct)
    {
        const string sql = """
            SELECT "Id","SupplierArticleNumber","OemNumbers","Brand","Description","UnitPriceForeign",
                   ("EnrichmentConfirmedAt" IS NOT NULL) AS confirmed,
                   "NeonOitmId", "EnrichmentPayloadJson"
            FROM public."staging_document_line"
            WHERE "DocumentId" = @doc AND "ReviewStatus" = 'create_new'
            ORDER BY "LineNumber";
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        var list = new List<PartsProvisioningLine>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new PartsProvisioningLine(
                Id:                    r.GetGuid(0),
                SupplierArticleNumber: r.IsDBNull(1) ? null : r.GetString(1),
                OemNumbers:            ParseOems(r, 2),
                Brand:                 r.IsDBNull(3) ? null : r.GetString(3),
                Description:           r.IsDBNull(4) ? null : r.GetString(4),
                UnitPriceForeign:      r.IsDBNull(5) ? null : r.GetDecimal(5),
                EnrichmentConfirmed:   !r.IsDBNull(6) && r.GetBoolean(6),
                NeonOitmId:            r.IsDBNull(7) ? null : r.GetInt64(7),
                EnrichmentPayloadJson: r.IsDBNull(8) ? null : r.GetString(8)));
        }
        return list;
    }

    private static PartsReviewLineRow Map(NpgsqlDataReader r) => new(
        Id:                    r.GetGuid(0),
        DocumentId:            r.GetGuid(1),
        LineNumber:            r.GetInt32(2),
        PageNumber:            r.IsDBNull(3) ? null : r.GetInt32(3),
        SupplierArticleNumber: r.IsDBNull(4) ? null : r.GetString(4),
        OemNumbers:            ParseOems(r, 5),
        Description:           r.IsDBNull(6) ? null : r.GetString(6),
        Brand:                 r.IsDBNull(7) ? null : r.GetString(7),
        Quantity:              r.IsDBNull(8) ? null : r.GetDecimal(8),
        Unit:                  r.IsDBNull(9) ? null : r.GetString(9),
        UnitPriceForeign:      r.IsDBNull(10) ? null : r.GetDecimal(10),
        DiscountPct:           r.IsDBNull(11) ? null : r.GetDecimal(11),
        LineTotalForeign:      r.IsDBNull(12) ? null : r.GetDecimal(12),
        IsPromotional:         !r.IsDBNull(13) && r.GetBoolean(13),
        ReviewStatus:          r.IsDBNull(14) ? "pending" : r.GetString(14),
        MatchedItemCode:       r.IsDBNull(15) ? null : r.GetString(15),
        GeneratedItemCode:     r.IsDBNull(16) ? null : r.GetString(16),
        EnrichmentSource:      r.IsDBNull(17) ? null : r.GetString(17),
        BorrowedFromArticle:   r.IsDBNull(18) ? null : r.GetString(18),
        EnrichmentConfirmedAt: r.IsDBNull(19) ? null : r.GetDateTime(19),
        CreateErrorMessage:    r.IsDBNull(20) ? null : r.GetString(20),
        MatchStrategy:         r.IsDBNull(21) ? null : r.GetString(21),
        BorrowedFromSupplier:  r.IsDBNull(22) ? null : r.GetString(22));

    private static List<string> ParseOems(NpgsqlDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(r.GetString(ordinal)) ?? new List<string>(); }
        catch (JsonException) { return new List<string>(); }
    }
}
