using System.Collections.Concurrent;

namespace SapOdooMiddleware.Pricing;

/// <summary>One item that failed during a bulk reprice run.</summary>
public sealed record BulkRepriceFailure(string ItemCode, string Error);

/// <summary>One applied line, kept (capped) for the result view.</summary>
public sealed record BulkRepriceApplied(
    string ItemCode, decimal? EurCost, decimal Rate,
    Dictionary<int, decimal> OldNet, Dictionary<int, decimal> NewNet);

/// <summary>Progress/result of a bulk reprice run. Status ∈ {running, done, stopped, failed}.</summary>
public sealed class BulkRepriceJob
{
    public Guid JobId { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; set; }
    public string Source { get; init; } = "";   // "excel" | "filter" | "list"
    public decimal? RateOverride { get; init; }
    public string? Note { get; init; }

    public int TotalItems { get; set; }
    public int Processed { get; set; }
    public int Applied { get; set; }
    public int Failed { get; set; }
    public int Warned { get; set; }
    public string? Error { get; set; }
    public IReadOnlyList<BulkRepriceFailure> Failures { get; set; } = Array.Empty<BulkRepriceFailure>();
    public IReadOnlyList<BulkRepriceApplied> AppliedSample { get; set; } = Array.Empty<BulkRepriceApplied>();

    public volatile string Status = "running";
}

/// <summary>
/// Runs bulk repricing in the BACKGROUND (one SAP Items.Update per item — hundreds of
/// items outlive any request timeout). One job at a time; per-item work goes through
/// <see cref="ILubesRepriceService.ApplyAsync"/> so single and bulk updates behave
/// identically (SAP + Neon + stamps + audit log + Odoo). Every write is an upsert, so
/// a stopped run can simply be started again with the remaining items.
/// </summary>
public sealed class LubesBulkRepriceJobService
{
    private const int MaxRecordedFailures = 200;
    private const int MaxAppliedSample = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<LubesBulkRepriceJobService> _logger;

    private readonly object _startLock = new();
    private BulkRepriceJob? _current;
    private CancellationTokenSource? _cts;

    public LubesBulkRepriceJobService(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        ILogger<LubesBulkRepriceJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>Latest job (running or finished), or null when none has run since startup.</summary>
    public BulkRepriceJob? Current => _current;

    public bool IsRunning => _current is { Status: "running" };

    /// <summary>Starts a bulk run (throws when one is already running).</summary>
    public BulkRepriceJob Start(
        IReadOnlyList<RepriceItemInput> items, decimal? rateOverride, string source, string? note)
    {
        lock (_startLock)
        {
            if (_current is { Status: "running" })
                throw new InvalidOperationException(
                    "A bulk reprice job is already running — wait for it to finish or stop it first.");

            var job = new BulkRepriceJob
            {
                JobId = Guid.NewGuid(),
                StartedAt = DateTime.UtcNow,
                Source = source,
                RateOverride = rateOverride,
                Note = note,
                TotalItems = items.Count,
            };
            _current = job;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            var ct = _cts.Token;
            _ = Task.Run(() => RunAsync(job, items, ct));
            return job;
        }
    }

    /// <summary>Requests a graceful stop after the current item.</summary>
    public bool Stop()
    {
        lock (_startLock)
        {
            if (_current is not { Status: "running" })
                return false;
            _cts?.Cancel();
            return true;
        }
    }

    private async Task RunAsync(BulkRepriceJob job, IReadOnlyList<RepriceItemInput> items, CancellationToken ct)
    {
        var failures = new List<BulkRepriceFailure>();
        var applied = new List<BulkRepriceApplied>();
        try
        {
            _logger.LogInformation(
                "Bulk reprice {JobId} starting: {Count} items (source={Source}, rate_override={Rate}).",
                job.JobId, items.Count, job.Source, job.RateOverride);

            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) { job.Status = "stopped"; break; }

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<ILubesRepriceService>();
                    var result = await svc.ApplyAsync(
                        item.ItemCode, item.EurCost, job.RateOverride, "bulk", job.Note, ct);

                    if (result.Applied)
                    {
                        job.Applied++;
                        if (result.Preview.Warnings.Count > 0) job.Warned++;
                        if (applied.Count < MaxAppliedSample && result.Preview.NewNet is not null)
                            applied.Add(new BulkRepriceApplied(
                                item.ItemCode, result.Preview.EurCost, result.Preview.Rate,
                                result.Preview.OldNet, result.Preview.NewNet));
                    }
                    else
                    {
                        job.Failed++;
                        if (failures.Count < MaxRecordedFailures)
                            failures.Add(new BulkRepriceFailure(item.ItemCode, result.Error ?? "unknown"));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    job.Status = "stopped";
                    break;
                }
                catch (Exception ex)
                {
                    job.Failed++;
                    if (failures.Count < MaxRecordedFailures)
                        failures.Add(new BulkRepriceFailure(item.ItemCode, ex.Message));
                    _logger.LogWarning(ex, "Bulk reprice {JobId}: item {ItemCode} failed.", job.JobId, item.ItemCode);
                }
                finally
                {
                    job.Processed++;
                }
            }

            job.Failures = failures;
            job.AppliedSample = applied;
            job.FinishedAt = DateTime.UtcNow;
            if (job.Status == "running") job.Status = "done";

            _logger.LogInformation(
                "Bulk reprice {JobId} {Status}: {Processed}/{Total} processed, {Applied} applied, " +
                "{Failed} failed, {Warned} with warnings.",
                job.JobId, job.Status, job.Processed, job.TotalItems, job.Applied, job.Failed, job.Warned);
        }
        catch (Exception ex)
        {
            job.Failures = failures;
            job.AppliedSample = applied;
            job.Error = ex.Message;
            job.FinishedAt = DateTime.UtcNow;
            job.Status = "failed";
            _logger.LogError(ex, "Bulk reprice {JobId} failed.", job.JobId);
        }
    }
}
