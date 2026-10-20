using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.PurchaseSync;

/// <summary>
/// Background worker for the SAP → Odoo purchases flow (spec v4 §6.1). Polls the purchase rows of
/// <c>ODOO_WEBHOOK_QUEUE</c> (EventType PO_UPSERT, GRPO_LINK, AP_INVOICE_*, AP_CREDIT_*) and
/// dispatches them to <see cref="PurchaseSyncHandlers"/>. Does nothing while the control centre's
/// cut-over date is empty (spec §6.6).
/// </summary>
public sealed class PurchaseSyncQueueProcessor : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<PurchaseSyncSettings> _settings;
    private readonly IOptionsMonitor<WebhookQueueSettings> _queue;
    private readonly ILogger<PurchaseSyncQueueProcessor> _logger;

    private DateTimeOffset _lastWaitLog = DateTimeOffset.MinValue;
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromMinutes(10);

    public PurchaseSyncQueueProcessor(
        IServiceScopeFactory scopes,
        IOptionsMonitor<PurchaseSyncSettings> settings,
        IOptionsMonitor<WebhookQueueSettings> queue,
        ILogger<PurchaseSyncQueueProcessor> logger)
    {
        _scopes = scopes;
        _settings = settings;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PurchaseSyncQueueProcessor starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var s = _settings.CurrentValue;
            var interval = TimeSpan.FromSeconds(Math.Max(10, s.PollingIntervalSeconds));

            if (!s.Enabled)
            {
                LogThrottled(LogLevel.Information, "PurchaseSyncQueueProcessor is disabled (PurchaseSync:Enabled=false).");
            }
            else if (string.IsNullOrWhiteSpace(_queue.CurrentValue.ConnectionString))
            {
                LogThrottled(LogLevel.Error, "PurchaseSyncQueueProcessor: WebhookQueue:ConnectionString is not configured.");
            }
            else
            {
                try
                {
                    await ProcessBatchAsync(s, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SqlException ex)
                {
                    _logger.LogError(ex, "PurchaseSyncQueueProcessor: SQL error during polling cycle (Number={Number}).", ex.Number);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PurchaseSyncQueueProcessor: unhandled error during polling cycle.");
                }
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("PurchaseSyncQueueProcessor stopping.");
    }

    private async Task ProcessBatchAsync(PurchaseSyncSettings s, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<PurchaseQueueRepository>();
        var mappingCache = scope.ServiceProvider.GetRequiredService<IIccMappingCache>();
        var handlers = scope.ServiceProvider.GetRequiredService<PurchaseSyncHandlers>();

        var rows = await repo.FetchPendingAsync(s.BatchSize, s.MaxRetries, ct);
        if (rows.Count == 0)
        {
            _logger.LogDebug("PurchaseSyncQueueProcessor: no pending purchase rows.");
            return;
        }

        IccMappings mappings;
        try
        {
            mappings = await mappingCache.GetAsync(ct);
        }
        catch (PurchaseSyncException ex)
        {
            LogThrottled(LogLevel.Warning,
                "PurchaseSyncQueueProcessor: {Count} purchase rows waiting; control-centre settings not readable: {Message}",
                rows.Count, ex.Message);
            return;
        }

        if (mappings.Settings.CutoverDate is null)
        {
            LogThrottled(LogLevel.Warning,
                "PurchaseSyncQueueProcessor: {Count} purchase rows waiting; icc.ap.sync.settings.cutover_date is empty, " +
                "nothing is processed until accounts sets it (spec §6.6).", rows.Count);
            return;
        }

        _logger.LogInformation("PurchaseSyncQueueProcessor: processing {Count} purchase rows.", rows.Count);

        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessRowAsync(repo, handlers, row, mappings, s, ct);
        }
    }

    private async Task ProcessRowAsync(
        PurchaseQueueRepository repo,
        PurchaseSyncHandlers handlers,
        PurchaseQueueRow row,
        IccMappings mappings,
        PurchaseSyncSettings s,
        CancellationToken ct)
    {
        _logger.LogInformation("PurchaseSync: row {Id} {EventType} ObjType={ObjectType} DocEntry={DocEntry} (retry {Retry}).",
            row.Id, row.EventType, row.ObjectType, row.DocEntry, row.RetryCount);

        await repo.SetStatusAsync(row.Id, "processing", ct);

        HandlerResult result;
        try
        {
            result = await handlers.HandleAsync(row, mappings, ct);
        }
        catch (PurchaseSyncException ex)
        {
            result = HandlerResult.Fail(ex.Code, ex.Message, ex.Retryable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await repo.SetStatusAsync(row.Id, "pending", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            // Odoo down, SQL hiccup, HTTP timeout, unexpected shapes: retry with the normal backoff.
            result = HandlerResult.Fail(ex.GetType().Name, ex.Message, retryable: true);
        }

        if (result.Success)
        {
            await repo.MarkDoneAsync(row.Id, result.Note, ct);
            _logger.LogInformation("PurchaseSync: row {Id} done: {Note}", row.Id, result.Note);
            return;
        }

        string error = $"{result.Code}: {result.Note}";
        if (!result.Retryable)
        {
            await repo.MarkFailedAsync(row.Id, row.RetryCount, "failed", error, ct);
            _logger.LogWarning("PurchaseSync: row {Id} failed (terminal): {Error}", row.Id, error);
            return;
        }

        int newRetry = row.RetryCount + 1;
        bool exhausted = newRetry >= s.MaxRetries;
        await repo.MarkFailedAsync(row.Id, newRetry, exhausted ? "failed" : "pending", error, ct);
        if (exhausted)
            _logger.LogError("PurchaseSync: row {Id} failed after {Max} attempts: {Error}", row.Id, s.MaxRetries, error);
        else
            _logger.LogWarning("PurchaseSync: row {Id} failed (attempt {Attempt}/{Max}), will retry: {Error}",
                row.Id, newRetry, s.MaxRetries, error);
    }

    private void LogThrottled(LogLevel level, string message, params object?[] args)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastWaitLog < WaitLogInterval) return;
        _lastWaitLog = now;
        _logger.Log(level, message, args);
    }
}
