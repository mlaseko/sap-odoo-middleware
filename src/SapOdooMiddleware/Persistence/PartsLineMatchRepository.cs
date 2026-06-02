using System.Text.Json;
using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>A pending staging line reduced to just what auto-match needs.</summary>
public sealed record PartsLineMatchCandidate(
    Guid Id,
    Guid DocumentId,
    IReadOnlyList<string> OemNumbers,
    string? SupplierArticleNumber,
    bool IsPromotional);

public interface IPartsLineMatchRepository
{
    /// <summary>Pending lines belonging to extracted documents, oldest first (worker sweep).</summary>
    Task<IReadOnlyList<PartsLineMatchCandidate>> ListPendingMatchCandidatesAsync(int limit, CancellationToken ct);

    Task SetMatchedAsync(Guid lineId, string itemCode, CancellationToken ct);
    Task SetReviewStatusAsync(Guid lineId, string status, CancellationToken ct);
}

/// <summary>
/// Review-state operations on parts_catalog.staging_document_line, separate from the Phase A
/// StagingPartsLineRepository so that file stays untouched. Connection per-tenant via ICompanyContext.
/// </summary>
public sealed class PartsLineMatchRepository : IPartsLineMatchRepository
{
    private readonly ICompanyContext _company;
    public PartsLineMatchRepository(ICompanyContext company) => _company = company;

    private string ConnectionString => _company.Current.Neon.ConnectionString;

    public async Task<IReadOnlyList<PartsLineMatchCandidate>> ListPendingMatchCandidatesAsync(int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT l."Id", l."DocumentId", l."OemNumbers", l."SupplierArticleNumber", l."IsPromotional"
            FROM public."staging_document_line" l
            JOIN public."staging_document" d ON d."Id" = l."DocumentId"
            WHERE l."ReviewStatus" = 'pending' AND d."Status" = 'extracted'
            ORDER BY d."ExtractedAt" NULLS LAST, l."LineNumber"
            LIMIT @limit;
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);

        var list = new List<PartsLineMatchCandidate>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new PartsLineMatchCandidate(
                Id:                    r.GetGuid(0),
                DocumentId:            r.GetGuid(1),
                OemNumbers:            ParseOems(r, 2),
                SupplierArticleNumber: r.IsDBNull(3) ? null : r.GetString(3),
                IsPromotional:         !r.IsDBNull(4) && r.GetBoolean(4)));
        }
        return list;
    }

    public async Task SetMatchedAsync(Guid lineId, string itemCode, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document_line"
            SET "ReviewStatus" = 'matched', "MatchedItemCode" = @code
            WHERE "Id" = @id;
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("code", itemCode);
        cmd.Parameters.AddWithValue("id", lineId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetReviewStatusAsync(Guid lineId, string status, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."staging_document_line" SET "ReviewStatus" = @status WHERE "Id" = @id;
            """;
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("id", lineId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static List<string> ParseOems(NpgsqlDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(r.GetString(ordinal)) ?? new List<string>(); }
        catch (JsonException) { return new List<string>(); }
    }
}
