using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.PurchaseSync;

namespace SapOdooMiddleware.Services.PurchaseSync;

/// <summary>One purchase row of ODOO_WEBHOOK_QUEUE.</summary>
public sealed record PurchaseQueueRow(
    int Id,
    string ObjectType,
    string TransType,
    int DocEntry,
    int? DocNum,
    string? CardCode,
    int RetryCount,
    string EventType,
    DateTime CreatedAt);

/// <summary>
/// The purchase worker's view of <c>[dbo].[ODOO_WEBHOOK_QUEUE]</c> (connection:
/// <c>WebhookQueue:ConnectionString</c>). Only rows whose EventType is one of the six purchase
/// values are ever read here; the delivery worker reads ObjectType 15 only.
/// </summary>
public sealed class PurchaseQueueRepository
{
    private readonly IOptionsMonitor<WebhookQueueSettings> _queue;

    public PurchaseQueueRepository(IOptionsMonitor<WebhookQueueSettings> queue) => _queue = queue;

    private string ConnectionString =>
        string.IsNullOrWhiteSpace(_queue.CurrentValue.ConnectionString)
            ? throw new InvalidOperationException("WebhookQueue:ConnectionString is not configured.")
            : _queue.CurrentValue.ConnectionString;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static string EventTypeList() =>
        string.Join(", ", PurchaseEventTypes.All.Select(e => $"'{e}'"));

    public async Task<List<PurchaseQueueRow>> FetchPendingAsync(int batchSize, int maxRetries, CancellationToken ct)
    {
        string sql = $"""
            SELECT TOP (@BatchSize)
                [Id], [ObjectType], [TransType], [DocEntry], [DocNum], [CardCode], [RetryCount], [EventType], [CreatedAt]
            FROM [dbo].[ODOO_WEBHOOK_QUEUE]
            WHERE [Status] = 'pending'
              AND [EventType] IN ({EventTypeList()})
              AND [RetryCount] < @MaxRetries
            ORDER BY [CreatedAt] ASC, [Id] ASC
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@BatchSize", SqlDbType.Int).Value = batchSize;
        cmd.Parameters.Add("@MaxRetries", SqlDbType.Int).Value = maxRetries;

        var rows = new List<PurchaseQueueRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new PurchaseQueueRow(
                Id: r.GetInt32(0),
                ObjectType: r.IsDBNull(1) ? string.Empty : r.GetString(1).Trim(),
                TransType: r.IsDBNull(2) ? string.Empty : r.GetString(2).Trim(),
                DocEntry: r.GetInt32(3),
                DocNum: r.IsDBNull(4) ? null : r.GetInt32(4),
                CardCode: r.IsDBNull(5) ? null : r.GetString(5).Trim(),
                RetryCount: r.GetInt32(6),
                EventType: r.IsDBNull(7) ? string.Empty : r.GetString(7).Trim(),
                CreatedAt: r.GetDateTime(8)));
        }
        return rows;
    }

    public async Task SetStatusAsync(int id, string status, CancellationToken ct)
    {
        const string sql = "UPDATE [dbo].[ODOO_WEBHOOK_QUEUE] SET [Status] = @Status WHERE [Id] = @Id";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@Status", SqlDbType.NVarChar, 20).Value = status;
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkDoneAsync(int id, string responseBody, CancellationToken ct)
    {
        const string sql = """
            UPDATE [dbo].[ODOO_WEBHOOK_QUEUE]
            SET [Status] = 'done', [ProcessedAt] = GETDATE(), [ResponseBody] = @ResponseBody, [ErrorMessage] = NULL
            WHERE [Id] = @Id
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@ResponseBody", SqlDbType.NVarChar, -1).Value = responseBody;
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(int id, int retryCount, string status, string errorMessage, CancellationToken ct)
    {
        const string sql = """
            UPDATE [dbo].[ODOO_WEBHOOK_QUEUE]
            SET [Status] = @Status, [RetryCount] = @RetryCount, [ErrorMessage] = @ErrorMessage,
                [ProcessedAt] = CASE WHEN @Status = 'failed' THEN GETDATE() ELSE [ProcessedAt] END
            WHERE [Id] = @Id
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@Status", SqlDbType.NVarChar, 20).Value = status;
        cmd.Parameters.Add("@RetryCount", SqlDbType.Int).Value = retryCount;
        cmd.Parameters.Add("@ErrorMessage", SqlDbType.NVarChar, -1).Value = errorMessage.Length > 4000 ? errorMessage[..4000] : errorMessage;
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Keys (ObjectType, DocEntry) that already have a queue row with TransType 'A' in any status
    /// other than 'failed' — used by the backfill to skip documents already queued or done.
    /// </summary>
    public async Task<HashSet<(string ObjectType, int DocEntry)>> GetQueuedAddKeysAsync(
        IReadOnlyCollection<string> objectTypes, CancellationToken ct)
    {
        var keys = new HashSet<(string, int)>();
        if (objectTypes.Count == 0) return keys;

        var typeList = string.Join(", ", objectTypes.Select((_, i) => $"@T{i}"));
        string sql = $"""
            SELECT DISTINCT [ObjectType], [DocEntry]
            FROM [dbo].[ODOO_WEBHOOK_QUEUE]
            WHERE [ObjectType] IN ({typeList})
              AND [TransType] = 'A'
              AND [Status] <> 'failed'
              AND [EventType] IN ({EventTypeList()})
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        int idx = 0;
        foreach (var t in objectTypes)
            cmd.Parameters.Add($"@T{idx++}", SqlDbType.NVarChar, 20).Value = t;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            keys.Add((r.GetString(0).Trim(), r.GetInt32(1)));
        return keys;
    }

    /// <summary>Inserts a pending 'A' row for a backfill candidate. Returns the new Id.</summary>
    public async Task<int> EnqueueAsync(SapBackfillCandidate c, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO [dbo].[ODOO_WEBHOOK_QUEUE]
                ([ObjectType], [TransType], [DocEntry], [DocNum], [CardCode], [Status], [RetryCount], [CreatedAt], [EventType])
            VALUES
                (@ObjectType, 'A', @DocEntry, @DocNum, @CardCode, 'pending', 0, GETDATE(), @EventType);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@ObjectType", SqlDbType.NVarChar, 20).Value = c.ObjectType;
        cmd.Parameters.Add("@DocEntry", SqlDbType.Int).Value = c.DocEntry;
        cmd.Parameters.Add("@DocNum", SqlDbType.Int).Value = c.DocNum;
        cmd.Parameters.Add("@CardCode", SqlDbType.NVarChar, 15).Value = (object?)c.CardCode ?? DBNull.Value;
        cmd.Parameters.Add("@EventType", SqlDbType.NVarChar, 50).Value = c.EventType;
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }
}
