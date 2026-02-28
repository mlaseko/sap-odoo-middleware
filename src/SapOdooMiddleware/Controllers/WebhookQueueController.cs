using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Api;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Manages the ODOO_WEBHOOK_QUEUE table: list entries by status and retry failed jobs
/// without needing to create a new order or cancellation.
/// </summary>
[ApiController]
[Route("api/webhook-queue")]
public class WebhookQueueController : ControllerBase
{
    private readonly IOptionsSnapshot<WebhookQueueSettings> _settings;
    private readonly ILogger<WebhookQueueController> _logger;

    /// <summary>
    /// Static flag so the EventType column migration runs at most once per
    /// application lifetime.
    /// </summary>
    private static bool _schemaEnsured;
    private static readonly object _schemaLock = new();

    public WebhookQueueController(
        IOptionsSnapshot<WebhookQueueSettings> settings,
        ILogger<WebhookQueueController> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/webhook-queue?status=failed
    /// Lists queue entries, optionally filtered by status (pending, processing, done, failed).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status = null)
    {
        using var connection = await OpenConnectionAsync();
        await EnsureEventTypeColumnAsync(connection);

        string sql;
        SqlCommand cmd;

        if (!string.IsNullOrWhiteSpace(status))
        {
            sql = """
                SELECT [Id], [DocEntry], [OdooSoId], [DeliveryDate], [Status],
                       [RetryCount], [ErrorMessage], [ResponseBody], [CreatedAt], [ProcessedAt],
                       [EventType]
                FROM [dbo].[ODOO_WEBHOOK_QUEUE]
                WHERE [Status] = @Status
                ORDER BY [CreatedAt] DESC
                """;
            cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@Status", status.Trim().ToLowerInvariant());
        }
        else
        {
            sql = """
                SELECT [Id], [DocEntry], [OdooSoId], [DeliveryDate], [Status],
                       [RetryCount], [ErrorMessage], [ResponseBody], [CreatedAt], [ProcessedAt],
                       [EventType]
                FROM [dbo].[ODOO_WEBHOOK_QUEUE]
                ORDER BY [CreatedAt] DESC
                """;
            cmd = new SqlCommand(sql, connection);
        }

        using (cmd)
        {
            var entries = new List<WebhookQueueEntryDto>();
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                entries.Add(MapEntry(reader));
            }

            _logger.LogInformation(
                "WebhookQueue list: returned {Count} entries (filter={Status})",
                entries.Count, status ?? "all");

            return Ok(ApiResponse<List<WebhookQueueEntryDto>>.Ok(entries));
        }
    }

    /// <summary>
    /// GET /api/webhook-queue/failed
    /// Returns only failed entries — the ones that exhausted all retries.
    /// Shows the error message, retry count, and timestamps for diagnosis.
    /// </summary>
    [HttpGet("failed")]
    public async Task<IActionResult> Failed()
    {
        using var connection = await OpenConnectionAsync();
        await EnsureEventTypeColumnAsync(connection);

        const string sql = """
            SELECT [Id], [DocEntry], [OdooSoId], [DeliveryDate], [Status],
                   [RetryCount], [ErrorMessage], [ResponseBody], [CreatedAt], [ProcessedAt],
                   [EventType]
            FROM [dbo].[ODOO_WEBHOOK_QUEUE]
            WHERE [Status] = 'failed'
            ORDER BY [CreatedAt] DESC
            """;

        using var cmd = new SqlCommand(sql, connection);
        var entries = new List<WebhookQueueEntryDto>();
        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            entries.Add(MapEntry(reader));
        }

        _logger.LogInformation("WebhookQueue failed: returned {Count} failed entries.", entries.Count);

        return Ok(ApiResponse<List<WebhookQueueEntryDto>>.Ok(
            entries,
            new Dictionary<string, object> { ["total_failed"] = entries.Count }));
    }

    /// <summary>
    /// POST /api/webhook-queue/{id}/retry
    /// Resets a single failed entry back to 'pending' with RetryCount = 0
    /// so the WebhookQueueProcessor picks it up again.
    /// </summary>
    [HttpPost("{id:int}/retry")]
    public async Task<IActionResult> Retry(int id)
    {
        using var connection = await OpenConnectionAsync();

        const string sql = """
            UPDATE [dbo].[ODOO_WEBHOOK_QUEUE]
            SET [Status] = 'pending',
                [RetryCount] = 0,
                [ErrorMessage] = NULL,
                [ProcessedAt] = NULL
            WHERE [Id] = @Id
              AND [Status] = 'failed'
            """;

        using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", id);

        int affected = await cmd.ExecuteNonQueryAsync();

        if (affected == 0)
        {
            _logger.LogWarning(
                "WebhookQueue retry: entry Id={Id} not found or not in 'failed' status.", id);
            return NotFound(ApiResponse<object>.Fail(
                $"Queue entry {id} not found or is not in 'failed' status."));
        }

        _logger.LogInformation("WebhookQueue retry: entry Id={Id} reset to pending.", id);
        return Ok(ApiResponse<object>.Ok(new { id, new_status = "pending" }));
    }

    /// <summary>
    /// POST /api/webhook-queue/retry-all
    /// Resets ALL failed entries back to 'pending' with RetryCount = 0.
    /// </summary>
    [HttpPost("retry-all")]
    public async Task<IActionResult> RetryAll()
    {
        using var connection = await OpenConnectionAsync();

        const string sql = """
            UPDATE [dbo].[ODOO_WEBHOOK_QUEUE]
            SET [Status] = 'pending',
                [RetryCount] = 0,
                [ErrorMessage] = NULL,
                [ProcessedAt] = NULL
            WHERE [Status] = 'failed'
            """;

        using var cmd = new SqlCommand(sql, connection);
        int affected = await cmd.ExecuteNonQueryAsync();

        _logger.LogInformation("WebhookQueue retry-all: {Count} failed entries reset to pending.", affected);
        return Ok(ApiResponse<object>.Ok(new { retried_count = affected }));
    }

    /// <summary>
    /// GET /api/webhook-queue/summary
    /// Returns a count of entries grouped by status, plus a total count.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary()
    {
        using var connection = await OpenConnectionAsync();

        const string sql = """
            SELECT [Status], COUNT(*) AS [Count]
            FROM [dbo].[ODOO_WEBHOOK_QUEUE]
            GROUP BY [Status]
            """;

        using var cmd = new SqlCommand(sql, connection);
        using var reader = await cmd.ExecuteReaderAsync();

        var summary = new Dictionary<string, int>();
        int total = 0;
        while (await reader.ReadAsync())
        {
            int count = reader.GetInt32(1);
            summary[reader.GetString(0)] = count;
            total += count;
        }
        summary["total"] = total;

        return Ok(ApiResponse<Dictionary<string, int>>.Ok(summary));
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connectionString = _settings.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("WebhookQueue:ConnectionString is not configured.");

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>
    /// Ensures the [EventType] column exists on the ODOO_WEBHOOK_QUEUE table.
    /// Runs at most once per application lifetime.
    /// </summary>
    private async Task EnsureEventTypeColumnAsync(SqlConnection connection)
    {
        if (_schemaEnsured)
            return;

        lock (_schemaLock)
        {
            if (_schemaEnsured)
                return;
        }

        const string sql = """
            IF NOT EXISTS (
                SELECT 1
                FROM sys.columns
                WHERE object_id = OBJECT_ID(N'[dbo].[ODOO_WEBHOOK_QUEUE]')
                  AND name = 'EventType'
            )
            ALTER TABLE [dbo].[ODOO_WEBHOOK_QUEUE]
                ADD [EventType] NVARCHAR(50) NOT NULL
                    CONSTRAINT DF_ODOO_WEBHOOK_QUEUE_EventType DEFAULT 'webhook';
            """;

        using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();

        lock (_schemaLock)
        {
            _schemaEnsured = true;
        }

        _logger.LogInformation("WebhookQueue: EventType column ensured on ODOO_WEBHOOK_QUEUE.");
    }

    private static WebhookQueueEntryDto MapEntry(SqlDataReader reader)
    {
        int eventTypeOrdinal = reader.GetOrdinal("EventType");
        return new WebhookQueueEntryDto
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            DocEntry = reader.GetInt32(reader.GetOrdinal("DocEntry")),
            OdooSoId = reader.IsDBNull(reader.GetOrdinal("OdooSoId"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("OdooSoId")),
            DeliveryDate = reader.IsDBNull(reader.GetOrdinal("DeliveryDate"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("DeliveryDate")),
            Status = reader.GetString(reader.GetOrdinal("Status")),
            RetryCount = reader.GetInt32(reader.GetOrdinal("RetryCount")),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage"))
                ? null
                : reader.GetString(reader.GetOrdinal("ErrorMessage")),
            ResponseBody = reader.IsDBNull(reader.GetOrdinal("ResponseBody"))
                ? null
                : reader.GetString(reader.GetOrdinal("ResponseBody")),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
            ProcessedAt = reader.IsDBNull(reader.GetOrdinal("ProcessedAt"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("ProcessedAt")),
            EventType = reader.IsDBNull(eventTypeOrdinal)
                ? "webhook"
                : reader.GetString(eventTypeOrdinal)
        };
    }
}
