using Npgsql;
using NpgsqlTypes;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>
/// One spare-parts staging document (header). Mirrors the Lubes <c>StagingDocumentRow</c> shape for
/// the plumbing/progress columns, but carries parts-specific header fields (SupplierName, Currency,
/// TotalAmount) and lives in the parts_catalog database. Connection string is resolved per-tenant
/// via <see cref="ICompanyContext"/> (always Autohub for this repo's callers).
/// </summary>
public record StagingPartsDocumentRow(
    Guid      Id,
    string    OriginalFilename,
    string    FilePath,
    string    FileSha256,
    int       PageCount,
    string    Status,
    string?   ValidationStatus,
    string?   ErrorMessage,
    DateTime  UploadedAt,
    DateTime? ExtractedAt,
    int       PagesProcessed,
    DateTime? CurrentPageStartedAt,
    decimal?  LastPageDurationSec,
    string?   SupplierName,
    string?   InvoiceNumber,
    DateTime? InvoiceDate,
    string?   Currency,
    decimal?  TotalAmount);

public record PartsHeaderUpdate(
    string?   SupplierName,
    string?   InvoiceNumber,
    DateTime? InvoiceDate,
    string?   Currency,
    decimal?  TotalAmount);

public interface IStagingPartsDocumentRepository
{
    Task<StagingPartsDocumentRow?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<StagingPartsDocumentRow?> GetBySha256Async(string sha256, CancellationToken ct);
    Task<IReadOnlyList<StagingPartsDocumentRow>> ListRecentAsync(int limit, CancellationToken ct);

    Task CreateAsync(Guid id, string originalFilename, string filePath, string sha256, CancellationToken ct);
    Task UpdateStatusAsync(Guid id, string status, string? errorMessage, CancellationToken ct);

    Task UpdateAfterExtractionAsync(
        Guid id, int pageCount, PartsHeaderUpdate? header,
        string validationStatus, string? errorMessage, string rawExtractionJson, CancellationToken ct);

    Task<IReadOnlyList<Guid>> ListPendingExtractionAsync(CancellationToken ct);

    // Live progress (same contract as the Lubes repo).
    Task SetTotalPagesAsync(Guid id, int pageCount, CancellationToken ct);
    Task MarkPageStartedAsync(Guid id, int pageNo, CancellationToken ct);
    Task RecordPageCompletedAsync(Guid id, int pageNo, double durationSec, CancellationToken ct);

    /// <summary>Phase B: transition the document to 'reviewed' and stamp the operator/time.</summary>
    Task MarkReviewedAsync(Guid id, string reviewedBy, CancellationToken ct);

    /// <summary>Delete a staging document and its lines (FK cascade). Does not touch SAP/parts_catalog.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);
}

public class StagingPartsDocumentRepository : IStagingPartsDocumentRepository
{
    private const string Columns =
        "\"Id\",\"OriginalFilename\",\"FilePath\",\"FileSha256\",\"PageCount\",\"Status\"," +
        "\"ValidationStatus\",\"ErrorMessage\",\"UploadedAt\",\"ExtractedAt\"," +
        "\"PagesProcessed\",\"CurrentPageStartedAt\",\"LastPageDurationSec\"," +
        "\"SupplierName\",\"InvoiceNumber\",\"InvoiceDate\",\"Currency\",\"TotalAmount\"";

    private readonly ICompanyContext _company;
    public StagingPartsDocumentRepository(ICompanyContext company) => _company = company;

    private string ConnectionString => _company.Current.Neon.ConnectionString;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync(ct);
        return c;
    }

    public async Task<StagingPartsDocumentRow?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {Columns} FROM public.\"staging_document\" WHERE \"Id\" = @id LIMIT 1;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task<StagingPartsDocumentRow?> GetBySha256Async(string sha256, CancellationToken ct)
    {
        var sql = $"SELECT {Columns} FROM public.\"staging_document\" WHERE \"FileSha256\" = @hash LIMIT 1;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("hash", sha256);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task<IReadOnlyList<StagingPartsDocumentRow>> ListRecentAsync(int limit, CancellationToken ct)
    {
        var sql = $"SELECT {Columns} FROM public.\"staging_document\" ORDER BY \"UploadedAt\" DESC LIMIT @limit;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);
        var list = new List<StagingPartsDocumentRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task CreateAsync(Guid id, string originalFilename, string filePath, string sha256, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO public."staging_document"
                ("Id","OriginalFilename","FilePath","FileSha256","Status")
            VALUES (@id,@name,@path,@hash,'uploaded');
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", originalFilename);
        cmd.Parameters.AddWithValue("path", filePath);
        cmd.Parameters.AddWithValue("hash", sha256);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateStatusAsync(Guid id, string status, string? errorMessage, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document" SET "Status" = @status, "ErrorMessage" = @err WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("err", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAfterExtractionAsync(
        Guid id, int pageCount, PartsHeaderUpdate? header,
        string validationStatus, string? errorMessage, string rawExtractionJson, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document" SET
                "Status"            = 'extracted',
                "ExtractedAt"       = now(),
                "PageCount"         = @pageCount,
                "SupplierName"      = @supplierName,
                "InvoiceNumber"     = @invoiceNumber,
                "InvoiceDate"       = @invoiceDate,
                "Currency"          = @currency,
                "TotalAmount"       = @totalAmount,
                "ValidationStatus"  = @validationStatus,
                "ErrorMessage"      = @err,
                "RawExtractionJson" = @rawJson
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("pageCount", pageCount);
        cmd.Parameters.AddWithValue("supplierName",  (object?)header?.SupplierName  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("invoiceNumber", (object?)header?.InvoiceNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("invoiceDate",   (object?)header?.InvoiceDate   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("currency",      (object?)header?.Currency      ?? DBNull.Value);
        cmd.Parameters.AddWithValue("totalAmount",   (object?)header?.TotalAmount   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("validationStatus", validationStatus);
        cmd.Parameters.AddWithValue("err", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("rawJson", NpgsqlDbType.Jsonb) { Value = rawExtractionJson });
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> ListPendingExtractionAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT "Id" FROM public."staging_document"
            WHERE "Status" IN ('uploaded','extracting')
            ORDER BY "UploadedAt";
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var list = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetGuid(0));
        return list;
    }

    public async Task SetTotalPagesAsync(Guid id, int pageCount, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document" SET "PageCount" = @pageCount, "PagesProcessed" = 0 WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("pageCount", pageCount);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkPageStartedAsync(Guid id, int pageNo, CancellationToken ct)
    {
        const string sql = "UPDATE public.\"staging_document\" SET \"CurrentPageStartedAt\" = NOW() WHERE \"Id\" = @id;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordPageCompletedAsync(Guid id, int pageNo, double durationSec, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document"
            SET "PagesProcessed" = @pageNo, "LastPageDurationSec" = @durationSec, "CurrentPageStartedAt" = NULL
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("pageNo", pageNo);
        cmd.Parameters.AddWithValue("durationSec", (decimal)durationSec);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkReviewedAsync(Guid id, string reviewedBy, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document"
            SET "Status" = 'reviewed', "ReviewedBy" = @by, "ReviewedAt" = NOW()
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("by", reviewedBy);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        // Lines are removed via the staging_document_line → staging_document ON DELETE CASCADE FK.
        const string sql = "DELETE FROM public.\"staging_document\" WHERE \"Id\" = @id;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static StagingPartsDocumentRow Map(NpgsqlDataReader r) => new(
        Id:                   r.GetGuid(0),
        OriginalFilename:     r.GetString(1),
        FilePath:             r.GetString(2),
        FileSha256:           r.GetString(3),
        PageCount:            r.IsDBNull(4)  ? 0    : r.GetInt32(4),
        Status:               r.GetString(5),
        ValidationStatus:     r.IsDBNull(6)  ? null : r.GetString(6),
        ErrorMessage:         r.IsDBNull(7)  ? null : r.GetString(7),
        UploadedAt:           r.GetDateTime(8),
        ExtractedAt:          r.IsDBNull(9)  ? null : r.GetDateTime(9),
        PagesProcessed:       r.IsDBNull(10) ? 0    : r.GetInt32(10),
        CurrentPageStartedAt: r.IsDBNull(11) ? null : r.GetDateTime(11),
        LastPageDurationSec:  r.IsDBNull(12) ? null : r.GetDecimal(12),
        SupplierName:         r.IsDBNull(13) ? null : r.GetString(13),
        InvoiceNumber:        r.IsDBNull(14) ? null : r.GetString(14),
        InvoiceDate:          r.IsDBNull(15) ? null : r.GetDateTime(15),
        Currency:             r.IsDBNull(16) ? null : r.GetString(16),
        TotalAmount:          r.IsDBNull(17) ? null : r.GetDecimal(17));
}
