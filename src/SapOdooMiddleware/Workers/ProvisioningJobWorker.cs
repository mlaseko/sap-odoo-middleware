using SapOdooMiddleware.ItemProvisioning;

namespace SapOdooMiddleware.Workers;

/// <summary>
/// Drains the async provisioning queue and runs the orchestrator off the request thread.
/// Processes one job at a time, which also serialises SAP DI-API (COM) access across jobs.
/// Because it runs in the background, the long cold-scrape + classifier work is no longer
/// bounded by the Cloudflare ~100s request timeout.
/// </summary>
public class ProvisioningJobWorker : BackgroundService
{
    private readonly IProvisioningJobStore _store;
    private readonly IServiceProvider _sp;
    private readonly ILogger<ProvisioningJobWorker> _logger;

    public ProvisioningJobWorker(
        IProvisioningJobStore store,
        IServiceProvider sp,
        ILogger<ProvisioningJobWorker> logger)
    {
        _store   = store;
        _sp      = sp;
        _logger  = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProvisioningJobWorker started.");
        try
        {
            await foreach (var jobId in _store.Reader.ReadAllAsync(stoppingToken))
                await ProcessAsync(jobId, stoppingToken);
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
        _logger.LogInformation(
            "Provisioning job {JobId} started for article {Article}.", jobId, request.ArticleNumber);

        try
        {
            using var scope = _sp.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ILubesItemProvisioningService>();
            var result = await service.ProvisionAsync(request, ct);
            _store.Complete(jobId, result);
            _logger.LogInformation(
                "Provisioning job {JobId} completed with status {Status}.", jobId, result.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Provisioning job {JobId} threw an unhandled exception.", jobId);
            _store.Fail(jobId, ex.Message);
        }
    }
}
