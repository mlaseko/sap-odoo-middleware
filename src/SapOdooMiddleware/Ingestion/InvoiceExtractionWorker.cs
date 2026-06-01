using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// Drains the document-extraction queue and runs <see cref="InvoiceExtractionJob"/> off the
/// request thread, one document at a time (also serialising vision calls to the DGX). On
/// startup it re-enqueues any rows left in 'uploaded'/'extracting' (recovery after a restart,
/// since the queue itself is in-memory).
/// </summary>
public class InvoiceExtractionWorker : BackgroundService
{
    private readonly IDocumentExtractionQueue _queue;
    private readonly IServiceProvider _sp;
    private readonly ILogger<InvoiceExtractionWorker> _logger;

    public InvoiceExtractionWorker(
        IDocumentExtractionQueue queue,
        IServiceProvider sp,
        ILogger<InvoiceExtractionWorker> logger)
    {
        _queue  = queue;
        _sp     = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InvoiceExtractionWorker started.");
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
            var pending = await docs.ListPendingExtractionAsync(ct);
            if (pending.Count == 0) return;

            _logger.LogInformation("Recovery: re-enqueuing {Count} pending document(s).", pending.Count);
            foreach (var id in pending)
                _queue.Enqueue(id);
        }
        catch (Exception ex)
        {
            // Neon may be unreachable at startup; don't crash the host — new uploads still work.
            _logger.LogError(ex, "Recovery sweep failed; continuing without it.");
        }
    }

    private async Task ProcessAsync(Guid documentId, CancellationToken ct)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<InvoiceExtractionJob>();
            await job.RunAsync(documentId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error processing document {Id}.", documentId);
        }
    }
}
