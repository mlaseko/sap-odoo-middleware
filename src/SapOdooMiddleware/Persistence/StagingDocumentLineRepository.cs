using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

public record StagingDocumentLineRow(
    Guid     Id,
    Guid     DocumentId,
    int      LineNo,
    int      PageNo,
    string?  ArticleNumber,
    string?  Description,
    string?  PackSize,
    decimal? UnitPrice,
    decimal? Quantity,
    string?  Unit,
    string?  CommodityCode,
    string?  Origin,
    decimal  DiscountPct,
    decimal? LineTotal,
    bool     IsPromotional,
    // Phase B review state (defaults keep existing positional construction in the extraction job valid).
    string   ReviewStatus       = "pending",
    string?  MatchedSku         = null,
    string?  CreatedSku         = null,
    DateTime? CreatedAt         = null,
    string?  CreateErrorMessage = null,
    DateTime? EditedAt          = null,
    string?  EditedBy           = null);

public interface IStagingDocumentLineRepository
{
    Task<IReadOnlyList<StagingDocumentLineRow>> ListByDocumentAsync(Guid documentId, CancellationToken ct);
    Task<StagingDocumentLineRow?> GetByIdAsync(Guid lineId, CancellationToken ct);
    Task InsertManyAsync(Guid documentId, IEnumerable<StagingDocumentLineRow> lines, CancellationToken ct);
    Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct);

    // --- Phase B review ---

    /// <summary>Update editable fields + EditedAt/EditedBy. When resetMatchState is true (article
    /// number changed) the line returns to 'pending' and MatchedSku is cleared. Returns updated row.</summary>
    Task<StagingDocumentLineRow?> UpdateEditableFieldsAsync(
        Guid lineId, string? articleNumber, string? description, string? packSize, string? unit,
        decimal? quantity, decimal? unitPrice, decimal discountPct, decimal? lineTotal,
        string? commodityCode, string? origin, bool isPromotional,
        string editedBy, bool resetMatchState, CancellationToken ct);

    Task SetReviewStatusAsync(Guid lineId, string status, string? matchedSku, CancellationToken ct);
    Task<int> BulkSetPendingToCreateNewAsync(Guid documentId, CancellationToken ct);
    Task RecordCreatedAsync(Guid lineId, string createdSku, CancellationToken ct);
    Task RecordCreateFailedAsync(Guid lineId, string error, CancellationToken ct);
    Task<Dictionary<string, int>> GetStatusCountsAsync(Guid documentId, CancellationToken ct);
}

public class StagingDocumentLineRepository : IStagingDocumentLineRepository
{
    private const string Cols =
        "\"Id\",\"DocumentId\",\"LineNo\",\"PageNo\",\"ArticleNumber\",\"Description\",\"PackSize\"," +
        "\"UnitPrice\",\"Quantity\",\"Unit\",\"CommodityCode\",\"Origin\",\"DiscountPct\",\"LineTotal\",\"IsPromotional\"," +
        "\"ReviewStatus\",\"MatchedSku\",\"CreatedSku\",\"CreatedAt\",\"CreateErrorMessage\",\"EditedAt\",\"EditedBy\"";

    private readonly string _conn;
    public StagingDocumentLineRepository(IOptions<NeonSettings> s) => _conn = s.Value.ConnectionString;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_conn);
        await c.OpenAsync(ct);
        return c;
    }

    private static StagingDocumentLineRow Map(NpgsqlDataReader r) => new(
        Id:            r.GetGuid(0),
        DocumentId:    r.GetGuid(1),
        LineNo:        r.GetInt32(2),
        PageNo:        r.GetInt32(3),
        ArticleNumber: r.IsDBNull(4)  ? null : r.GetString(4),
        Description:   r.IsDBNull(5)  ? null : r.GetString(5),
        PackSize:      r.IsDBNull(6)  ? null : r.GetString(6),
        UnitPrice:     r.IsDBNull(7)  ? null : r.GetDecimal(7),
        Quantity:      r.IsDBNull(8)  ? null : r.GetDecimal(8),
        Unit:          r.IsDBNull(9)  ? null : r.GetString(9),
        CommodityCode: r.IsDBNull(10) ? null : r.GetString(10),
        Origin:        r.IsDBNull(11) ? null : r.GetString(11),
        DiscountPct:   r.IsDBNull(12) ? 0m  : r.GetDecimal(12),
        LineTotal:     r.IsDBNull(13) ? null : r.GetDecimal(13),
        IsPromotional: !r.IsDBNull(14) && r.GetBoolean(14),
        ReviewStatus:       r.IsDBNull(15) ? "pending" : r.GetString(15),
        MatchedSku:         r.IsDBNull(16) ? null : r.GetString(16),
        CreatedSku:         r.IsDBNull(17) ? null : r.GetString(17),
        CreatedAt:          r.IsDBNull(18) ? null : r.GetDateTime(18),
        CreateErrorMessage: r.IsDBNull(19) ? null : r.GetString(19),
        EditedAt:           r.IsDBNull(20) ? null : r.GetDateTime(20),
        EditedBy:           r.IsDBNull(21) ? null : r.GetString(21));

    public async Task<IReadOnlyList<StagingDocumentLineRow>> ListByDocumentAsync(Guid documentId, CancellationToken ct)
    {
        var sql = $"SELECT {Cols} FROM public.\"staging_document_line\" WHERE \"DocumentId\" = @doc ORDER BY \"LineNo\";";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);

        var list = new List<StagingDocumentLineRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task<StagingDocumentLineRow?> GetByIdAsync(Guid lineId, CancellationToken ct)
    {
        var sql = $"SELECT {Cols} FROM public.\"staging_document_line\" WHERE \"Id\" = @id LIMIT 1;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", lineId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task InsertManyAsync(Guid documentId, IEnumerable<StagingDocumentLineRow> lines, CancellationToken ct)
    {
        var rows = lines.ToList();
        if (rows.Count == 0) return;

        var sb = new StringBuilder(
            "INSERT INTO public.\"staging_document_line\" " +
            "(\"Id\",\"DocumentId\",\"LineNo\",\"PageNo\",\"ArticleNumber\",\"Description\",\"PackSize\"," +
            "\"UnitPrice\",\"Quantity\",\"Unit\",\"CommodityCode\",\"Origin\",\"DiscountPct\",\"LineTotal\",\"IsPromotional\") VALUES ");

        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand { Connection = conn };

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (i > 0) sb.Append(',');
            sb.Append($"(@id{i},@doc,@ln{i},@pg{i},@art{i},@desc{i},@pk{i},@up{i},@qty{i},@un{i},@cc{i},@or{i},@dp{i},@lt{i},@promo{i})");

            cmd.Parameters.AddWithValue($"id{i}", row.Id);
            cmd.Parameters.AddWithValue($"ln{i}", row.LineNo);
            cmd.Parameters.AddWithValue($"pg{i}", row.PageNo);
            cmd.Parameters.AddWithValue($"art{i}",   (object?)row.ArticleNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"desc{i}",  (object?)row.Description   ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"pk{i}",    (object?)row.PackSize      ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"up{i}",    (object?)row.UnitPrice     ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"qty{i}",   (object?)row.Quantity      ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"un{i}",    (object?)row.Unit          ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"cc{i}",    (object?)row.CommodityCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"or{i}",    (object?)row.Origin        ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"dp{i}",    row.DiscountPct);
            cmd.Parameters.AddWithValue($"lt{i}",    (object?)row.LineTotal     ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"promo{i}", row.IsPromotional);
        }
        sb.Append(';');

        cmd.Parameters.AddWithValue("doc", documentId);
        cmd.CommandText = sb.ToString();
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct)
    {
        const string sql = "DELETE FROM public.\"staging_document_line\" WHERE \"DocumentId\" = @doc;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<StagingDocumentLineRow?> UpdateEditableFieldsAsync(
        Guid lineId, string? articleNumber, string? description, string? packSize, string? unit,
        decimal? quantity, decimal? unitPrice, decimal discountPct, decimal? lineTotal,
        string? commodityCode, string? origin, bool isPromotional,
        string editedBy, bool resetMatchState, CancellationToken ct)
    {
        var resetClause = resetMatchState ? ", \"ReviewStatus\" = 'pending', \"MatchedSku\" = NULL" : "";
        var sql = $"""
            UPDATE public."staging_document_line" SET
                "ArticleNumber" = @art, "Description" = @desc, "PackSize" = @pk, "Unit" = @un,
                "Quantity" = @qty, "UnitPrice" = @up, "DiscountPct" = @dp, "LineTotal" = @lt,
                "CommodityCode" = @cc, "Origin" = @or, "IsPromotional" = @promo,
                "EditedAt" = now(), "EditedBy" = @by{resetClause}
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("art",   (object?)articleNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("desc",  (object?)description   ?? DBNull.Value);
            cmd.Parameters.AddWithValue("pk",    (object?)packSize      ?? DBNull.Value);
            cmd.Parameters.AddWithValue("un",    (object?)unit          ?? DBNull.Value);
            cmd.Parameters.AddWithValue("qty",   (object?)quantity      ?? DBNull.Value);
            cmd.Parameters.AddWithValue("up",    (object?)unitPrice     ?? DBNull.Value);
            cmd.Parameters.AddWithValue("dp",    discountPct);
            cmd.Parameters.AddWithValue("lt",    (object?)lineTotal     ?? DBNull.Value);
            cmd.Parameters.AddWithValue("cc",    (object?)commodityCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("or",    (object?)origin        ?? DBNull.Value);
            cmd.Parameters.AddWithValue("promo", isPromotional);
            cmd.Parameters.AddWithValue("by",    editedBy);
            cmd.Parameters.AddWithValue("id",    lineId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return await GetByIdAsync(lineId, ct);
    }

    public async Task SetReviewStatusAsync(Guid lineId, string status, string? matchedSku, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document_line"
            SET "ReviewStatus" = @status, "MatchedSku" = @sku
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("sku", (object?)matchedSku ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", lineId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> BulkSetPendingToCreateNewAsync(Guid documentId, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document_line"
            SET "ReviewStatus" = 'create_new', "MatchedSku" = NULL
            WHERE "DocumentId" = @doc AND "ReviewStatus" = 'pending';
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordCreatedAsync(Guid lineId, string createdSku, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document_line"
            SET "ReviewStatus" = 'created', "CreatedSku" = @sku, "CreatedAt" = now(), "CreateErrorMessage" = NULL
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("sku", createdSku);
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
        cmd.Parameters.AddWithValue("err", error);
        cmd.Parameters.AddWithValue("id", lineId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Dictionary<string, int>> GetStatusCountsAsync(Guid documentId, CancellationToken ct)
    {
        const string sql = """
            SELECT "ReviewStatus", count(*) FROM public."staging_document_line"
            WHERE "DocumentId" = @doc
            GROUP BY "ReviewStatus";
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);

        var counts = new Dictionary<string, int>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) counts[r.GetString(0)] = (int)r.GetInt64(1);
        return counts;
    }
}
