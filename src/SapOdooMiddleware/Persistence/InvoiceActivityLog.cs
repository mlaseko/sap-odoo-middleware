using Npgsql;
using NpgsqlTypes;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>One history entry. LineId / LineNumber are null for an invoice-level entry.</summary>
public sealed record InvoiceActivityEntry(
    long Id, Guid DocumentId, Guid? LineId, int? LineNumber, string Action, string Detail, string Actor, DateTime At);

/// <summary>A line-level entry to record: which line, and (optionally) a detail specific to it.</summary>
public sealed record ActivityLine(Guid LineId, string? Detail = null);

public interface IInvoiceActivityLog
{
    /// <summary>Record an invoice-level entry. Best effort: never throws.</summary>
    Task RecordAsync(Guid documentId, string action, string detail, string actor, CancellationToken ct);

    /// <summary>
    /// Record one entry per line (in one round trip). A line's own Detail wins over the shared detail.
    /// The line number is looked up at write time. Best effort: never throws.
    /// </summary>
    Task RecordLinesAsync(Guid documentId, string action, string detail, string actor,
        IReadOnlyList<ActivityLine> lines, CancellationToken ct);

    /// <summary>Newest first. With lineId, that line's entries only.</summary>
    Task<IReadOnlyList<InvoiceActivityEntry>> ListAsync(Guid documentId, Guid? lineId, int limit, CancellationToken ct);
}

/// <summary>
/// invoice_activity in parts_catalog (Autohub tenant via ICompanyContext). Writes are best effort: the history
/// must never fail the action it describes, so a write error is logged and swallowed (e.g. before the
/// migration is run).
/// </summary>
public sealed class InvoiceActivityLog : IInvoiceActivityLog
{
    private readonly ICompanyContext _company;
    private readonly ILogger<InvoiceActivityLog> _logger;

    public InvoiceActivityLog(ICompanyContext company, ILogger<InvoiceActivityLog> logger)
    {
        _company = company;
        _logger = logger;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    public Task RecordAsync(Guid documentId, string action, string detail, string actor, CancellationToken ct) =>
        WriteAsync(documentId, action, detail, actor, Array.Empty<ActivityLine>(), ct);

    public Task RecordLinesAsync(Guid documentId, string action, string detail, string actor,
        IReadOnlyList<ActivityLine> lines, CancellationToken ct) =>
        lines.Count == 0 ? Task.CompletedTask : WriteAsync(documentId, action, detail, actor, lines, ct);

    private async Task WriteAsync(Guid documentId, string action, string detail, string actor,
        IReadOnlyList<ActivityLine> lines, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_company.Current.Neon.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand { Connection = conn };
            if (lines.Count == 0)
            {
                cmd.CommandText = """
                    INSERT INTO public.invoice_activity (document_id, action, detail, actor)
                    VALUES (@doc, @action, @detail, @actor);
                    """;
                cmd.Parameters.AddWithValue("detail", Clip(detail, 2000));
            }
            else
            {
                // One row per line; the line number is taken from the line as it is now.
                cmd.CommandText = """
                    INSERT INTO public.invoice_activity (document_id, line_id, line_number, action, detail, actor)
                    SELECT @doc, x.id, l."LineNumber", @action, x.detail, @actor
                    FROM unnest(@ids, @details) AS x(id, detail)
                    LEFT JOIN public."staging_document_line" l ON l."Id" = x.id;
                    """;
                cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                    { Value = lines.Select(l => l.LineId).ToArray() });
                cmd.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Array | NpgsqlDbType.Text)
                    { Value = lines.Select(l => Clip(l.Detail ?? detail, 2000)).ToArray() });
            }
            cmd.Parameters.AddWithValue("doc", documentId);
            cmd.Parameters.AddWithValue("action", Clip(action, 40));
            cmd.Parameters.AddWithValue("actor", Clip(string.IsNullOrWhiteSpace(actor) ? "operator" : actor, 100));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not record invoice activity '{Action}' for document {Doc}.", action, documentId);
        }
    }

    public async Task<IReadOnlyList<InvoiceActivityEntry>> ListAsync(Guid documentId, Guid? lineId, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT id, document_id, line_id, line_number, action, detail, actor, at
            FROM public.invoice_activity
            WHERE document_id = @doc AND (@line::uuid IS NULL OR line_id = @line)
            ORDER BY at DESC, id DESC
            LIMIT @lim;
            """;
        await using var conn = new NpgsqlConnection(_company.Current.Neon.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        cmd.Parameters.Add(new NpgsqlParameter("line", NpgsqlDbType.Uuid) { Value = (object?)lineId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("lim", Math.Clamp(limit, 1, 2000));
        var list = new List<InvoiceActivityEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new InvoiceActivityEntry(
                r.GetInt64(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetInt32(3),
                r.GetString(4), r.GetString(5), r.GetString(6), r.GetDateTime(7)));
        return list;
    }
}
