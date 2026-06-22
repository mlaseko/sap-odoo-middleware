using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>
/// One extracted spare-parts line. OEM cross-references are kept together as a JSON array on the
/// line (one invoice line = one staging row); they are an attribute of the line, not separate rows.
/// Phase A is read/insert only — the review/match/create columns are populated in Phase B.
/// </summary>
public record StagingPartsLineRow(
    Guid          Id,
    Guid          DocumentId,
    int           LineNumber,
    int?          PageNumber,
    string?       SupplierArticleNumber,
    List<string>  OemNumbers,
    string?       Description,
    string?       Brand,
    decimal?      Quantity,
    string?       Unit,
    decimal?      UnitPriceForeign,
    decimal?      DiscountPct,
    decimal?      LineTotalForeign,
    bool          IsPromotional);

public interface IStagingPartsLineRepository
{
    Task<IReadOnlyList<StagingPartsLineRow>> ListByDocumentAsync(Guid documentId, CancellationToken ct);
    Task InsertManyAsync(Guid documentId, IEnumerable<StagingPartsLineRow> lines, CancellationToken ct);
    Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct);
}

public class StagingPartsLineRepository : IStagingPartsLineRepository
{
    private const string Cols =
        "\"Id\",\"DocumentId\",\"LineNumber\",\"PageNumber\",\"SupplierArticleNumber\",\"OemNumbers\"," +
        "\"Description\",\"Brand\",\"Quantity\",\"Unit\",\"UnitPriceForeign\",\"DiscountPct\"," +
        "\"LineTotalForeign\",\"IsPromotional\"";

    private readonly ICompanyContext _company;
    public StagingPartsLineRepository(ICompanyContext company) => _company = company;

    private string ConnectionString => _company.Current.Neon.ConnectionString;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync(ct);
        return c;
    }

    public async Task<IReadOnlyList<StagingPartsLineRow>> ListByDocumentAsync(Guid documentId, CancellationToken ct)
    {
        var sql = $"SELECT {Cols} FROM public.\"staging_document_line\" WHERE \"DocumentId\" = @doc ORDER BY \"LineNumber\";";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        var list = new List<StagingPartsLineRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task InsertManyAsync(Guid documentId, IEnumerable<StagingPartsLineRow> lines, CancellationToken ct)
    {
        var rows = lines.ToList();
        if (rows.Count == 0) return;

        var sb = new StringBuilder(
            "INSERT INTO public.\"staging_document_line\" " +
            "(\"Id\",\"DocumentId\",\"LineNumber\",\"PageNumber\",\"SupplierArticleNumber\",\"OemNumbers\"," +
            "\"Description\",\"Brand\",\"Quantity\",\"Unit\",\"UnitPriceForeign\",\"DiscountPct\"," +
            "\"LineTotalForeign\",\"IsPromotional\") VALUES ");

        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand { Connection = conn };

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (i > 0) sb.Append(',');
            sb.Append($"(@id{i},@doc,@ln{i},@pg{i},@sa{i},@oem{i},@desc{i},@br{i},@qty{i},@un{i},@up{i},@dp{i},@lt{i},@promo{i})");

            cmd.Parameters.AddWithValue($"id{i}", row.Id);
            cmd.Parameters.AddWithValue($"ln{i}", row.LineNumber);
            cmd.Parameters.AddWithValue($"pg{i}",   (object?)row.PageNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"sa{i}",   (object?)row.SupplierArticleNumber ?? DBNull.Value);
            cmd.Parameters.Add(new NpgsqlParameter($"oem{i}", NpgsqlDbType.Jsonb)
            {
                Value = (row.OemNumbers is { Count: > 0 })
                    ? JsonSerializer.Serialize(row.OemNumbers)
                    : (object)DBNull.Value
            });
            cmd.Parameters.AddWithValue($"desc{i}", (object?)row.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"br{i}",   (object?)row.Brand ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"qty{i}",  (object?)row.Quantity ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"un{i}",   (object?)row.Unit ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"up{i}",   (object?)row.UnitPriceForeign ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"dp{i}",   (object?)row.DiscountPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"lt{i}",   (object?)row.LineTotalForeign ?? DBNull.Value);
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

    private static StagingPartsLineRow Map(NpgsqlDataReader r) => new(
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
        IsPromotional:         !r.IsDBNull(13) && r.GetBoolean(13));

    private static List<string> ParseOems(NpgsqlDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return new List<string>();
        var json = r.GetString(ordinal);
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }
}
