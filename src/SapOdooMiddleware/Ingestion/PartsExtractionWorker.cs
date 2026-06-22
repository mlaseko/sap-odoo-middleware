using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// Drains the Autohub extraction queue and runs <see cref="PartsExtractionJob"/> off the request
/// thread, one document at a time. Because there is no HTTP request in the worker, each scope's
/// tenant is set to Autohub explicitly before any tenant-aware service is resolved. On startup it
/// re-enqueues parts_catalog rows left in 'uploaded'/'extracting' (recovery after a restart).
/// No auto-match handoff — that arrives in Autohub Phase B.
/// </summary>
public class PartsExtractionWorker : BackgroundService
{
    private readonly IPartsExtractionQueue _queue;
    private readonly IServiceProvider _sp;
    private readonly ILogger<PartsExtractionWorker> _logger;

    public PartsExtractionWorker(
        IPartsExtractionQueue queue,
        IServiceProvider sp,
        ILogger<PartsExtractionWorker> logger)
    {
        _queue  = queue;
        _sp     = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PartsExtractionWorker started.");
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
            using var scope = CreateAutohubScope();
            var docs = scope.ServiceProvider.GetRequiredService<IStagingPartsDocumentRepository>();
            var pending = await docs.ListPendingExtractionAsync(ct);
            if (pending.Count == 0) return;

            _logger.LogInformation("Autohub recovery: re-enqueuing {Count} pending document(s).", pending.Count);
            foreach (var id in pending)
                _queue.Enqueue(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Autohub recovery sweep failed; continuing without it.");
        }
    }

    private async Task ProcessAsync(Guid documentId, CancellationToken ct)
    {
        try
        {
            using var scope = CreateAutohubScope();
            var job = scope.ServiceProvider.GetRequiredService<PartsExtractionJob>();
            await job.RunAsync(documentId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error processing Autohub document {Id}.", documentId);
        }
    }

    /// <summary>Creates a DI scope whose CompanyContext is pinned to Autohub.</summary>
    private IServiceScope CreateAutohubScope()
    {
        var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<CompanyContext>().SetCompany(CompanyContext.AutohubKey);
        return scope;
    }
}
