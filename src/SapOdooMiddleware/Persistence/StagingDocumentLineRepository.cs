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
    bool     IsPromotional);

public interface IStagingDocumentLineRepository
{
    Task<IReadOnlyList<StagingDocumentLineRow>> ListByDocumentAsync(Guid documentId, CancellationToken ct);
    Task InsertManyAsync(Guid documentId, IEnumerable<StagingDocumentLineRow> lines, CancellationToken ct);
    Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct);
}

public class StagingDocumentLineRepository : IStagingDocumentLineRepository
{
    private readonly string _conn;
    public StagingDocumentLineRepository(IOptions<NeonSettings> s) => _conn = s.Value.ConnectionString;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_conn);
        await c.OpenAsync(ct);
        return c;
    }

    public async Task<IReadOnlyList<StagingDocumentLineRow>> ListByDocumentAsync(Guid documentId, CancellationToken ct)
    {
        const string sql = """
            SELECT "Id","DocumentId","LineNo","PageNo","ArticleNumber","Description","PackSize",
                   "UnitPrice","Quantity","Unit","CommodityCode","Origin","DiscountPct","LineTotal","IsPromotional"
            FROM public."staging_document_line"
            WHERE "DocumentId" = @doc
            ORDER BY "LineNo";
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);

        var list = new List<StagingDocumentLineRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new StagingDocumentLineRow(
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
                IsPromotional: !r.IsDBNull(14) && r.GetBoolean(14)));
        }
        return list;
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
}
