using SapOdooMiddleware.ItemProvisioning;

namespace SapOdooMiddleware.Workers;

/// <summary>
/// Drains the inquiry preview queue (POST /api/items/preview). Previews only scrape and classify — no SAP DI-API —
/// so a few run side by side: a cold scrape of one code does not hold up a cached one.
/// </summary>
public class ItemPreviewJobWorker : BackgroundService
{
    private const int Parallelism = 3;

    private readonly IItemPreviewJobStore _store;
    private readonly IServiceProvider _sp;
    private readonly ILogger<ItemPreviewJobWorker> _logger;

    public ItemPreviewJobWorker(IItemPreviewJobStore store, IServiceProvider sp, ILogger<ItemPreviewJobWorker> logger)
    {
        _store  = store;
        _sp     = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ItemPreviewJobWorker started ({Parallelism} at a time).", Parallelism);
        var readers = Enumerable.Range(0, Parallelism).Select(_ => DrainAsync(stoppingToken));
        await Task.WhenAll(readers);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var jobId in _store.Reader.ReadAllAsync(ct))
                await ProcessAsync(jobId, ct);
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down — expected.
        }
    }

    private async Task ProcessAsync(string jobId, CancellationToken ct)
    {
        var request = _store.GetRequest(jobId);
        if (request is null) return;

        _store.MarkRunning(jobId);
        try
        {
            using var scope = _sp.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ILubesItemProvisioningService>();
            var result = await service.PreviewAsync(request, ct);
            _store.Complete(jobId, result);
            _logger.LogInformation("Preview job {JobId} for {Article}: {Status}.", jobId, request.ArticleNumber, result.Status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _store.Fail(jobId, "The middleware restarted; ask again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preview job {JobId} for {Article} threw.", jobId, request.ArticleNumber);
            _store.Fail(jobId, ex.Message);
        }
    }
}
