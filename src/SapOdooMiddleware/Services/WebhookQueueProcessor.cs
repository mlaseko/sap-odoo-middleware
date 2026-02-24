using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Odoo;

namespace SapOdooMiddleware.Services;

/// <summary>
/// Background service that polls the ODOO_WEBHOOK_QUEUE table in SQL Server and
/// calls IOdooService.ConfirmDeliveryAsync() for each pending delivery entry.
/// </summary>
public class WebhookQueueProcessor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<WebhookQueueSettings> _settingsMonitor;
    private readonly ILogger<WebhookQueueProcessor> _logger;

    public WebhookQueueProcessor(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<WebhookQueueSettings> settingsMonitor,
        ILogger<WebhookQueueProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _settingsMonitor = settingsMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebhookQueueProcessor starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _settingsMonitor.CurrentValue;

            if (!settings.Enabled)
            {
                _logger.LogInformation("WebhookQueueProcessor is disabled. Skipping poll.");
                await Task.Delay(TimeSpan.FromSeconds(settings.PollingIntervalSeconds), stoppingToken);
                continue;
            }

            if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            {
                _logger.LogError("WebhookQueueProcessor: ConnectionString is not configured. Skipping poll.");
                await Task.Delay(TimeSpan.FromSeconds(settings.PollingIntervalSeconds), stoppingToken);
                continue;
            }

            try
            {
                await ProcessBatchAsync(settings, stoppingToken);
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex,
                    "WebhookQueueProcessor: SQL error during polling cycle (Number={SqlErrorNumber}). " +
                    "Check the ConnectionString and that ODOO_WEBHOOK_QUEUE exists.",
                    ex.Number);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebhookQueueProcessor: Unhandled error during polling cycle.");
            }

            await Task.Delay(TimeSpan.FromSeconds(settings.PollingIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("WebhookQueueProcessor stopping.");
    }

    private async Task ProcessBatchAsync(WebhookQueueSettings settings, CancellationToken stoppingToken)
    {
        using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(stoppingToken);

        var entries = await FetchPendingEntriesAsync(connection, settings, stoppingToken);

        if (entries.Count == 0)
        {
            _logger.LogDebug("WebhookQueueProcessor: No pending entries found.");
            return;
        }

        _logger.LogInformation("WebhookQueueProcessor: Processing {Count} pending entries.", entries.Count);

        foreach (var entry in entries)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            await ProcessEntryAsync(connection, entry, settings, stoppingToken);
        }
    }

    private static async Task<List<QueueEntry>> FetchPendingEntriesAsync(
        SqlConnection connection,
        WebhookQueueSettings settings,
        CancellationToken stoppingToken)
    {
        const string sql = """
            SELECT TOP (@BatchSize)
                [Id], [DocEntry], [OdooSoId], [DeliveryDate], [RetryCount]
            FROM [dbo].[ODOO_WEBHOOK_QUEUE]
            WHERE [Status] = 'pending'
              AND [RetryCount] < @MaxRetries
            ORDER BY [CreatedAt] ASC
            """;

        using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@BatchSize", settings.BatchSize);
        cmd.Parameters.AddWithValue("@MaxRetries", settings.MaxRetries);

        var entries = new List<QueueEntry>();
        using var reader = await cmd.ExecuteReaderAsync(stoppingToken);
        int colId = reader.GetOrdinal("Id");
        int colDocEntry = reader.GetOrdinal("DocEntry");
        int colOdooSoId = reader.GetOrdinal("OdooSoId");
        int colDeliveryDate = reader.GetOrdinal("DeliveryDate");
        int colRetryCount = reader.GetOrdinal("RetryCount");

        while (await reader.ReadAsync(stoppingToken))
        {
            entries.Add(new QueueEntry
            {
                Id = reader.GetInt32(colId),
                DocEntry = reader.GetInt32(colDocEntry),
                OdooSoId = reader.IsDBNull(colOdooSoId) ? string.Empty : reader.GetString(colOdooSoId),
                DeliveryDate = reader.IsDBNull(colDeliveryDate) ? null : reader.GetDateTime(colDeliveryDate),
                RetryCount = reader.GetInt32(colRetryCount)
            });
        }

        return entries;
    }

    private async Task ProcessEntryAsync(
        SqlConnection connection,
        QueueEntry entry,
        WebhookQueueSettings settings,
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "WebhookQueueProcessor: Processing queue entry Id={Id}, DocEntry={DocEntry}, OdooSoId={OdooSoId}.",
            entry.Id, entry.DocEntry, entry.OdooSoId);

        await SetStatusAsync(connection, entry.Id, "processing", stoppingToken);

        try
        {
            var request = new DeliveryUpdateRequest
            {
                UOdooSoId = entry.OdooSoId,
                SapDeliveryNo = entry.DocEntry.ToString(),
                DeliveryDate = entry.DeliveryDate,
                Status = "delivered"
            };

            using var scope = _scopeFactory.CreateScope();
            var odooService = scope.ServiceProvider.GetRequiredService<IOdooService>();
            var response = await odooService.ConfirmDeliveryAsync(request);

            string responseBody = System.Text.Json.JsonSerializer.Serialize(response);
            await MarkDoneAsync(connection, entry.Id, responseBody, stoppingToken);

            _logger.LogInformation(
                "WebhookQueueProcessor: Odoo delivery confirmed for queue entry Id={Id}. " +
                "OdooSoId={OdooSoId}, PickingId={PickingId}, PickingName={PickingName}, " +
                "State={State}, SapDeliveryNo={SapDeliveryNo}.",
                entry.Id, response.UOdooSoId, response.PickingId, response.PickingName,
                response.State, response.SapDeliveryNo);
        }
        catch (Exception ex)
        {
            int newRetryCount = entry.RetryCount + 1;
            bool maxRetriesReached = newRetryCount >= settings.MaxRetries;
            string newStatus = maxRetriesReached ? "failed" : "pending";

            if (maxRetriesReached)
            {
                _logger.LogError(ex,
                    "WebhookQueueProcessor: Entry Id={Id} (OdooSoId={OdooSoId}, DocEntry={DocEntry}) " +
                    "permanently failed after {MaxRetries} retries. Marking as failed. Error: {ErrorMessage}",
                    entry.Id, entry.OdooSoId, entry.DocEntry, settings.MaxRetries, ex.Message);
            }
            else
            {
                _logger.LogWarning(ex,
                    "WebhookQueueProcessor: Entry Id={Id} (OdooSoId={OdooSoId}, DocEntry={DocEntry}) " +
                    "failed (attempt {Attempt}/{MaxRetries}). Will retry. Error: {ErrorMessage}",
                    entry.Id, entry.OdooSoId, entry.DocEntry, newRetryCount, settings.MaxRetries, ex.Message);
            }

            await MarkFailedAsync(connection, entry.Id, newRetryCount, newStatus, ex.Message, stoppingToken);
        }
    }

    private static async Task SetStatusAsync(
        SqlConnection connection,
        int id,
        string status,
        CancellationToken stoppingToken)
    {
        const string sql = "UPDATE [dbo].[ODOO_WEBHOOK_QUEUE] SET [Status] = @Status WHERE [Id] = @Id";
        using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Status", status);
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(stoppingToken);
    }

    private static async Task MarkDoneAsync(
        SqlConnection connection,
        int id,
        string responseBody,
        CancellationToken stoppingToken)
    {
        const string sql = """
            UPDATE [dbo].[ODOO_WEBHOOK_QUEUE]
            SET [Status] = 'done',
                [ProcessedAt] = GETDATE(),
                [ResponseBody] = @ResponseBody
            WHERE [Id] = @Id
            """;
        using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@ResponseBody", responseBody);
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(stoppingToken);
    }

    private static async Task MarkFailedAsync(
        SqlConnection connection,
        int id,
        int retryCount,
        string status,
        string errorMessage,
        CancellationToken stoppingToken)
    {
        const string sql = """
            UPDATE [dbo].[ODOO_WEBHOOK_QUEUE]
            SET [Status] = @Status,
                [RetryCount] = @RetryCount,
                [ErrorMessage] = @ErrorMessage
            WHERE [Id] = @Id
            """;
        using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Status", status);
        cmd.Parameters.AddWithValue("@RetryCount", retryCount);
        cmd.Parameters.AddWithValue("@ErrorMessage", (object)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(stoppingToken);
    }

    private sealed class QueueEntry
    {
        public int Id { get; init; }
        public int DocEntry { get; init; }
        public string OdooSoId { get; init; } = string.Empty;
        public DateTime? DeliveryDate { get; init; }
        public int RetryCount { get; init; }
    }
}
