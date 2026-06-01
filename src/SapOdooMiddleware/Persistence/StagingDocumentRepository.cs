using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

public record StagingDocumentRow(
    Guid      Id,
    string    OriginalFilename,
    string    FilePath,
    string    FileHash,
    long      FileSizeBytes,
    int?      PageCount,
    string    Status,
    string    DocumentType,
    string?   Supplier,
    string?   InvoiceNumber,
    DateTime? InvoiceDate,
    string?   SalesOrder,
    string?   DeliveryNoteRef,
    string?   CustomerName,
    string?   CustomerAccount,
    string?   Currency,
    decimal?  Subtotal,
    decimal?  Freight,
    decimal?  TotalNet,
    decimal?  TaxAmount,
    decimal?  InvoiceTotal,
    string?   PaymentTerms,
    DateTime? DueDate,
    string?   ValidationStatus,
    string?   ValidationNotes,
    string?   ErrorMessage,
    DateTime  UploadedAt,
    DateTime? ExtractedAt,
    int       PagesProcessed,
    DateTime? CurrentPageStartedAt,
    decimal?  LastPageDurationSec);

public record InvoiceHeaderUpdate(
    string? InvoiceNumber, DateTime? InvoiceDate, string? SalesOrder, string? DeliveryNoteRef,
    string? CustomerName, string? CustomerAccount, string? Currency);

public record InvoiceFooterUpdate(
    decimal? Subtotal, decimal? Freight, decimal? TotalNet, decimal? TaxAmount,
    decimal? InvoiceTotal, string? PaymentTerms, DateTime? DueDate);

public interface IStagingDocumentRepository
{
    Task<StagingDocumentRow?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<StagingDocumentRow?> GetByHashAsync(string fileHash, CancellationToken ct);
    Task<IReadOnlyList<StagingDocumentRow>> ListRecentAsync(int limit, CancellationToken ct);

    /// <summary>Inserts a new 'uploaded' row using the caller-supplied Id (matches the on-disk folder).</summary>
    Task CreateAsync(
        Guid id, string originalFilename, string filePath, string fileHash, long fileSizeBytes,
        CancellationToken ct);

    Task UpdateStatusAsync(Guid id, string status, string? errorMessage, CancellationToken ct);

    Task UpdateAfterExtractionAsync(
        Guid id,
        int pageCount,
        InvoiceHeaderUpdate? header,
        InvoiceFooterUpdate? footer,
        string validationStatus,
        string? validationNotes,
        string rawExtractionJson,
        CancellationToken ct);

    /// <summary>Document ids still needing extraction (recovery sweep on worker startup).</summary>
    Task<IReadOnlyList<Guid>> ListPendingExtractionAsync(CancellationToken ct);

    // --- Live extraction progress ---

    /// <summary>Set total page count and reset processed count (called once after PDF render).</summary>
    Task SetTotalPagesAsync(Guid documentId, int pageCount, CancellationToken ct);

    /// <summary>Mark a page as started (stamps CurrentPageStartedAt = NOW()).</summary>
    Task MarkPageStartedAsync(Guid documentId, int pageNo, CancellationToken ct);

    /// <summary>Record a page as completed: advance PagesProcessed, store duration, clear the start stamp.</summary>
    Task RecordPageCompletedAsync(Guid documentId, int pageNo, double durationSec, CancellationToken ct);
}

public class StagingDocumentRepository : IStagingDocumentRepository
{
    private const string Columns =
        "\"Id\",\"OriginalFilename\",\"FilePath\",\"FileHash\",\"FileSizeBytes\",\"PageCount\"," +
        "\"Status\",\"DocumentType\",\"Supplier\",\"InvoiceNumber\",\"InvoiceDate\",\"SalesOrder\"," +
        "\"DeliveryNoteRef\",\"CustomerName\",\"CustomerAccount\",\"Currency\",\"Subtotal\",\"Freight\"," +
        "\"TotalNet\",\"TaxAmount\",\"InvoiceTotal\",\"PaymentTerms\",\"DueDate\",\"ValidationStatus\"," +
        "\"ValidationNotes\",\"ErrorMessage\",\"UploadedAt\",\"ExtractedAt\"," +
        "\"PagesProcessed\",\"CurrentPageStartedAt\",\"LastPageDurationSec\"";

    private readonly string _conn;
    public StagingDocumentRepository(IOptions<NeonSettings> s) => _conn = s.Value.ConnectionString;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_conn);
        await c.OpenAsync(ct);
        return c;
    }

    public async Task<StagingDocumentRow?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {Columns} FROM public.\"staging_document\" WHERE \"Id\" = @id LIMIT 1;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task<StagingDocumentRow?> GetByHashAsync(string fileHash, CancellationToken ct)
    {
        var sql = $"SELECT {Columns} FROM public.\"staging_document\" WHERE \"FileHash\" = @hash LIMIT 1;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("hash", fileHash);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task<IReadOnlyList<StagingDocumentRow>> ListRecentAsync(int limit, CancellationToken ct)
    {
        var sql = $"SELECT {Columns} FROM public.\"staging_document\" ORDER BY \"UploadedAt\" DESC LIMIT @limit;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);
        var list = new List<StagingDocumentRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task CreateAsync(
        Guid id, string originalFilename, string filePath, string fileHash, long fileSizeBytes,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO public."staging_document"
                ("Id","OriginalFilename","FilePath","FileHash","FileSizeBytes","Status")
            VALUES (@id,@name,@path,@hash,@size,'uploaded');
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", originalFilename);
        cmd.Parameters.AddWithValue("path", filePath);
        cmd.Parameters.AddWithValue("hash", fileHash);
        cmd.Parameters.AddWithValue("size", fileSizeBytes);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateStatusAsync(Guid id, string status, string? errorMessage, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document"
            SET "Status" = @status, "ErrorMessage" = @err
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("err", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAfterExtractionAsync(
        Guid id, int pageCount, InvoiceHeaderUpdate? header, InvoiceFooterUpdate? footer,
        string validationStatus, string? validationNotes, string rawExtractionJson, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document" SET
                "Status"            = 'extracted',
                "ExtractedAt"       = now(),
                "PageCount"         = @pageCount,
                "InvoiceNumber"     = @invoiceNumber,
                "InvoiceDate"       = @invoiceDate,
                "SalesOrder"        = @salesOrder,
                "DeliveryNoteRef"   = @deliveryNoteRef,
                "CustomerName"      = @customerName,
                "CustomerAccount"   = @customerAccount,
                "Currency"          = @currency,
                "Subtotal"          = @subtotal,
                "Freight"           = @freight,
                "TotalNet"          = @totalNet,
                "TaxAmount"         = @taxAmount,
                "InvoiceTotal"      = @invoiceTotal,
                "PaymentTerms"      = @paymentTerms,
                "DueDate"           = @dueDate,
                "ValidationStatus"  = @validationStatus,
                "ValidationNotes"   = @validationNotes,
                "RawExtractionJson" = @rawJson,
                "ErrorMessage"      = NULL
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("pageCount", pageCount);
        cmd.Parameters.AddWithValue("invoiceNumber",   (object?)header?.InvoiceNumber   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("invoiceDate",     (object?)header?.InvoiceDate     ?? DBNull.Value);
        cmd.Parameters.AddWithValue("salesOrder",      (object?)header?.SalesOrder      ?? DBNull.Value);
        cmd.Parameters.AddWithValue("deliveryNoteRef", (object?)header?.DeliveryNoteRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("customerName",    (object?)header?.CustomerName    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("customerAccount", (object?)header?.CustomerAccount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("currency",        (object?)header?.Currency        ?? DBNull.Value);
        cmd.Parameters.AddWithValue("subtotal",        (object?)footer?.Subtotal        ?? DBNull.Value);
        cmd.Parameters.AddWithValue("freight",         (object?)footer?.Freight         ?? DBNull.Value);
        cmd.Parameters.AddWithValue("totalNet",        (object?)footer?.TotalNet        ?? DBNull.Value);
        cmd.Parameters.AddWithValue("taxAmount",       (object?)footer?.TaxAmount       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("invoiceTotal",    (object?)footer?.InvoiceTotal    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("paymentTerms",    (object?)footer?.PaymentTerms    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("dueDate",         (object?)footer?.DueDate         ?? DBNull.Value);
        cmd.Parameters.AddWithValue("validationStatus", validationStatus);
        cmd.Parameters.AddWithValue("validationNotes", (object?)validationNotes ?? DBNull.Value);
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

    public async Task SetTotalPagesAsync(Guid documentId, int pageCount, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document"
            SET "PageCount" = @pageCount, "PagesProcessed" = 0
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("pageCount", pageCount);
        cmd.Parameters.AddWithValue("id", documentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkPageStartedAsync(Guid documentId, int pageNo, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document"
            SET "CurrentPageStartedAt" = NOW()
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", documentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordPageCompletedAsync(Guid documentId, int pageNo, double durationSec, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document"
            SET "PagesProcessed" = @pageNo,
                "LastPageDurationSec" = @durationSec,
                "CurrentPageStartedAt" = NULL
            WHERE "Id" = @id;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("pageNo", pageNo);
        cmd.Parameters.AddWithValue("durationSec", (decimal)durationSec);
        cmd.Parameters.AddWithValue("id", documentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static StagingDocumentRow Map(NpgsqlDataReader r) => new(
        Id:               r.GetGuid(0),
        OriginalFilename: r.GetString(1),
        FilePath:         r.GetString(2),
        FileHash:         r.GetString(3),
        FileSizeBytes:    r.GetInt64(4),
        PageCount:        r.IsDBNull(5)  ? null : r.GetInt32(5),
        Status:           r.GetString(6),
        DocumentType:     r.GetString(7),
        Supplier:         r.IsDBNull(8)  ? null : r.GetString(8),
        InvoiceNumber:    r.IsDBNull(9)  ? null : r.GetString(9),
        InvoiceDate:      r.IsDBNull(10) ? null : r.GetDateTime(10),
        SalesOrder:       r.IsDBNull(11) ? null : r.GetString(11),
        DeliveryNoteRef:  r.IsDBNull(12) ? null : r.GetString(12),
        CustomerName:     r.IsDBNull(13) ? null : r.GetString(13),
        CustomerAccount:  r.IsDBNull(14) ? null : r.GetString(14),
        Currency:         r.IsDBNull(15) ? null : r.GetString(15),
        Subtotal:         r.IsDBNull(16) ? null : r.GetDecimal(16),
        Freight:          r.IsDBNull(17) ? null : r.GetDecimal(17),
        TotalNet:         r.IsDBNull(18) ? null : r.GetDecimal(18),
        TaxAmount:        r.IsDBNull(19) ? null : r.GetDecimal(19),
        InvoiceTotal:     r.IsDBNull(20) ? null : r.GetDecimal(20),
        PaymentTerms:     r.IsDBNull(21) ? null : r.GetString(21),
        DueDate:          r.IsDBNull(22) ? null : r.GetDateTime(22),
        ValidationStatus: r.IsDBNull(23) ? null : r.GetString(23),
        ValidationNotes:  r.IsDBNull(24) ? null : r.GetString(24),
        ErrorMessage:     r.IsDBNull(25) ? null : r.GetString(25),
        UploadedAt:       r.GetDateTime(26),
        ExtractedAt:      r.IsDBNull(27) ? null : r.GetDateTime(27),
        PagesProcessed:       r.IsDBNull(28) ? 0    : r.GetInt32(28),
        CurrentPageStartedAt: r.IsDBNull(29) ? null : r.GetDateTime(29),
        LastPageDurationSec:  r.IsDBNull(30) ? null : r.GetDecimal(30));
}
