using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>An invoice line as the purchase order needs it: its SAP item and its PL01 when Bulk Create made it.</summary>
public sealed record AutohubPoSourceLine(
    Guid Id, int LineNumber, string ReviewStatus, bool IsPromotional, string? ItemCode,
    string? SupplierArticleNumber, string? Description, string? Brand,
    decimal? Quantity, decimal? UnitPriceForeign, decimal? DiscountPct, decimal? Pl01Tzs);

/// <summary>The SAP vendor remembered for an invoice supplier name.</summary>
public sealed record AutohubSupplierVendor(string SupplierName, string CardCode, string? CardName);

/// <summary>The SAP purchase order created from an invoice.</summary>
public sealed record AutohubPurchaseOrderRecord(
    Guid DocumentId, string CardCode, int DocEntry, int DocNum, int LineCount, decimal DocTotal,
    string CreatedBy, DateTime CreatedAt);

public interface IAutohubPurchaseOrderRepository
{
    /// <summary>Every line of the invoice, in line order.</summary>
    Task<IReadOnlyList<AutohubPoSourceLine>> ListLinesAsync(Guid documentId, CancellationToken ct);

    /// <summary>The vendor remembered for this supplier name, or null.</summary>
    Task<AutohubSupplierVendor?> GetVendorAsync(string supplierName, CancellationToken ct);

    /// <summary>Remember (insert or replace) the vendor for this supplier name.</summary>
    Task SaveVendorAsync(string supplierName, string cardCode, string? cardName, string user, CancellationToken ct);

    /// <summary>The purchase order already created from this invoice, or null.</summary>
    Task<AutohubPurchaseOrderRecord?> GetPurchaseOrderAsync(Guid documentId, CancellationToken ct);

    /// <summary>Record the purchase order created from the invoice (first record wins).</summary>
    Task SavePurchaseOrderAsync(AutohubPurchaseOrderRecord record, CancellationToken ct);
}

/// <summary>Autohub Neon (Parts_Catalog); tables from migrations/2026-11-18__autohub_purchase_order.sql.</summary>
public sealed class AutohubPurchaseOrderRepository : IAutohubPurchaseOrderRepository
{
    private readonly ICompanyContext _company;
    public AutohubPurchaseOrderRepository(ICompanyContext company) => _company = company;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_company.Current.Neon.ConnectionString);
        await c.OpenAsync(ct);
        return c;
    }

    /// <summary>Supplier names are matched trimmed and case-insensitively.</summary>
    internal static string SupplierKey(string supplierName) => supplierName.Trim().ToUpperInvariant();

    public async Task<IReadOnlyList<AutohubPoSourceLine>> ListLinesAsync(Guid documentId, CancellationToken ct)
    {
        const string sql = """
            SELECT "Id", "LineNumber", "ReviewStatus", "IsPromotional",
                   COALESCE(NULLIF(TRIM("MatchedItemCode"), ''), NULLIF(TRIM("GeneratedItemCode"), '')),
                   "SupplierArticleNumber", "Description", "Brand",
                   "Quantity", "UnitPriceForeign", "DiscountPct", "Pl01Tzs"
            FROM public."staging_document_line"
            WHERE "DocumentId" = @doc
            ORDER BY "LineNumber";
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        var list = new List<AutohubPoSourceLine>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new AutohubPoSourceLine(
                r.GetGuid(0), r.GetInt32(1), r.IsDBNull(2) ? "" : r.GetString(2), !r.IsDBNull(3) && r.GetBoolean(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetDecimal(9),
                r.IsDBNull(10) ? null : r.GetDecimal(10), r.IsDBNull(11) ? null : r.GetDecimal(11)));
        }
        return list;
    }

    public async Task<AutohubSupplierVendor?> GetVendorAsync(string supplierName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(supplierName)) return null;
        const string sql = """
            SELECT supplier_name, card_code, card_name FROM public.autohub_supplier_vendor WHERE supplier_key = @key;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("key", SupplierKey(supplierName));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new AutohubSupplierVendor(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    public async Task SaveVendorAsync(string supplierName, string cardCode, string? cardName, string user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(supplierName)) return;
        const string sql = """
            INSERT INTO public.autohub_supplier_vendor (supplier_key, supplier_name, card_code, card_name, updated_by, updated_at)
            VALUES (@key, @name, @code, @cardName, @user, now())
            ON CONFLICT (supplier_key) DO UPDATE SET
                supplier_name = EXCLUDED.supplier_name, card_code = EXCLUDED.card_code,
                card_name = EXCLUDED.card_name, updated_by = EXCLUDED.updated_by, updated_at = now();
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("key", SupplierKey(supplierName));
        cmd.Parameters.AddWithValue("name", supplierName.Trim());
        cmd.Parameters.AddWithValue("code", cardCode);
        cmd.Parameters.AddWithValue("cardName", (object?)cardName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("user", user);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AutohubPurchaseOrderRecord?> GetPurchaseOrderAsync(Guid documentId, CancellationToken ct)
    {
        const string sql = """
            SELECT document_id, card_code, doc_entry, doc_num, line_count, doc_total, created_by, created_at
            FROM public.autohub_purchase_order WHERE document_id = @doc;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new AutohubPurchaseOrderRecord(
            r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetDecimal(5),
            r.GetString(6), r.GetDateTime(7));
    }

    public async Task SavePurchaseOrderAsync(AutohubPurchaseOrderRecord record, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO public.autohub_purchase_order
                (document_id, card_code, doc_entry, doc_num, line_count, doc_total, created_by, created_at)
            VALUES (@doc, @code, @entry, @num, @lines, @total, @user, now())
            ON CONFLICT (document_id) DO NOTHING;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", record.DocumentId);
        cmd.Parameters.AddWithValue("code", record.CardCode);
        cmd.Parameters.AddWithValue("entry", record.DocEntry);
        cmd.Parameters.AddWithValue("num", record.DocNum);
        cmd.Parameters.AddWithValue("lines", record.LineCount);
        cmd.Parameters.AddWithValue("total", record.DocTotal);
        cmd.Parameters.AddWithValue("user", record.CreatedBy);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
