using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// Drains the auto-match queue and runs <see cref="InvoiceAutoMatchJob"/> off the request thread,
/// one document at a time. On startup it re-enqueues extracted documents that were never
/// auto-matched (recovery after a restart, since the queue is in-memory).
/// </summary>
public class InvoiceAutoMatchWorker : BackgroundService
{
    private readonly IDocumentAutoMatchQueue _queue;
    private readonly IServiceProvider _sp;
    private readonly ILogger<InvoiceAutoMatchWorker> _logger;

    public InvoiceAutoMatchWorker(
        IDocumentAutoMatchQueue queue,
        IServiceProvider sp,
        ILogger<InvoiceAutoMatchWorker> logger)
    {
        _queue = queue;
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InvoiceAutoMatchWorker started.");
        await RecoverPendingAsync(stoppingToken);

        try
        {
            await foreach (var documentId in _queue.Reader.ReadAllAsync(stoppingToken))
                await ProcessAsync(documentId, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down — expected.
        }
    }

    private async Task RecoverPendingAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var docs = scope.ServiceProvider.GetRequiredService<IStagingDocumentRepository>();
            var pending = await docs.ListNeedingAutoMatchAsync(ct);
            if (pending.Count == 0) return;

            _logger.LogInformation("Auto-match recovery: re-enqueuing {Count} document(s).", pending.Count);
            foreach (var id in pending)
                _queue.Enqueue(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-match recovery sweep failed; continuing without it.");
        }
    }

    private async Task ProcessAsync(Guid documentId, CancellationToken ct)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<InvoiceAutoMatchJob>();
            await job.RunAsync(documentId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error auto-matching document {Id}.", documentId);
        }
    }
}
