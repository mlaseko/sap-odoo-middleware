using System.Collections.Concurrent;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>Progress/result of one async Bulk Create run. Status ∈ {running, done, failed}.</summary>
public sealed class AutohubBulkCreateJob
{
    public Guid JobId { get; init; }
    public Guid DocumentId { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; set; }

    // Set BEFORE Status flips to a terminal value (Status is the last write a reader observes).
    public int Attempted { get; set; }
    public int Created { get; set; }
    public int NeedsConfirmation { get; set; }
    public int Failed { get; set; }
    public string? Error { get; set; }
    public IReadOnlyList<PartsBulkCreateFailure> Failures { get; set; } = Array.Empty<PartsBulkCreateFailure>();

    public volatile string Status = "running";
}

/// <summary>
/// Runs Autohub Bulk Create in the BACKGROUND so it survives the reverse-proxy/client request timeout
/// (creating 250+ items takes minutes; a single synchronous HTTP call gets cut off at ~100s). The
/// operator POSTs to start a job, gets a job id, and polls for progress/result; the work runs on the
/// application lifetime, not the request. In-memory (a restart loses job records, but created items are
/// already committed — re-run to finish). One running job per document.
/// </summary>
public sealed class AutohubBulkCreateJobService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AutohubBulkCreateJobService> _logger;
    private readonly ConcurrentDictionary<Guid, AutohubBulkCreateJob> _jobs = new();

    public AutohubBulkCreateJobService(
        IServiceScopeFactory scopeFactory, IHostApplicationLifetime lifetime, ILogger<AutohubBulkCreateJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _lifetime = lifetime;
        _logger = logger;
    }

    public AutohubBulkCreateJob? Get(Guid jobId) => _jobs.TryGetValue(jobId, out var j) ? j : null;

    public AutohubBulkCreateJob? GetRunningForDocument(Guid documentId) =>
        _jobs.Values.FirstOrDefault(j => j.DocumentId == documentId && j.Status == "running");

    /// <summary>Start (or return the already-running) Bulk Create job for a document.</summary>
    public AutohubBulkCreateJob Start(Guid documentId)
    {
        if (GetRunningForDocument(documentId) is { } running) return running;

        var job = new AutohubBulkCreateJob { JobId = Guid.NewGuid(), DocumentId = documentId, StartedAt = DateTime.UtcNow };
        _jobs[job.JobId] = job;
        _ = Task.Run(() => RunAsync(job));
        return job;
    }

    private async Task RunAsync(AutohubBulkCreateJob job)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<CompanyContext>().SetCompany(CompanyContext.AutohubKey);
            var creator = scope.ServiceProvider.GetRequiredService<PartsItemCreationService>();

            // Application-stopping token (NOT a request token) — the run survives the proxy/client timeout.
            var result = await creator.BulkCreateAsync(job.DocumentId, _lifetime.ApplicationStopping);

            job.Attempted = result.Attempted;
            job.Created = result.Created;
            job.NeedsConfirmation = result.NeedsConfirmation;
            job.Failed = result.Failed;
            job.Failures = result.Failures;
            job.FinishedAt = DateTime.UtcNow;
            job.Status = "done";
            _logger.LogInformation(
                "Autohub async bulk-create {JobId} ({Doc}) complete: created {Created}/{Attempted}, needsConfirmation {Needs}, failed {Failed}.",
                job.JobId, job.DocumentId, result.Created, result.Attempted, result.NeedsConfirmation, result.Failed);
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.FinishedAt = DateTime.UtcNow;
            job.Status = "failed";
            _logger.LogError(ex, "Autohub async bulk-create {JobId} ({Doc}) failed.", job.JobId, job.DocumentId);
        }
    }
}
