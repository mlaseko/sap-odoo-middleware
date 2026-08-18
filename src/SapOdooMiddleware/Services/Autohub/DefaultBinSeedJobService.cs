using SapOdooMiddleware.Models.Inventory;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>One item that failed during the seeding run.</summary>
public sealed record DefaultBinSeedFailure(string ItemCode, string Error);

/// <summary>Progress/result of a default-bin seeding run. Status ∈ {running, done, stopped, failed}.</summary>
public sealed class DefaultBinSeedJob
{
    public Guid JobId { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; set; }
    public bool Overwrite { get; init; }
    /// <summary>Max item groups this run processes (0 = no limit). Use ~5 for the spike.</summary>
    public int Limit { get; init; }

    /// <summary>Item groups the run will attempt (after skip/overwrite filtering and limit).</summary>
    public int TotalItems { get; set; }
    public int ProcessedItems { get; set; }
    /// <summary>Items where Items.Update() actually wrote a default.</summary>
    public int UpdatedItems { get; set; }
    /// <summary>Items where every targeted warehouse default was already correct.</summary>
    public int UnchangedItems { get; set; }
    public int FailedItems { get; set; }
    public string? Error { get; set; }
    public IReadOnlyList<DefaultBinSeedFailure> Failures { get; set; } = Array.Empty<DefaultBinSeedFailure>();

    public volatile string Status = "running";
}

/// <summary>
/// Runs the one-time default-bin seeding job (spec §10) in the BACKGROUND: ~7-8k
/// Items.Update() calls take far longer than any request timeout. Seeds OITW.DftBinAbs
/// from the top stocked non-system bin per item-warehouse, grouped so each item gets
/// ONE update covering all its warehouses.
///
/// Naturally resumable: with overwrite=false every re-run skips pairs whose default is
/// already set, so a stopped/crashed run just continues where it left off. Low-risk by
/// design — it only fills empty defaults (unless overwrite=true), DefaultBinEnforced
/// stays off, and it is fully reversible in the B1 client.
/// </summary>
public sealed class DefaultBinSeedJobService
{
    private const int MaxRecordedFailures = 100;

    private readonly IAutohubInventorySqlService _sql;
    private readonly IAutohubSapB1Service _sap;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DefaultBinSeedJobService> _logger;

    private readonly object _startLock = new();
    private DefaultBinSeedJob? _current;
    private CancellationTokenSource? _cts;

    public DefaultBinSeedJobService(
        IAutohubInventorySqlService sql,
        IAutohubSapB1Service sap,
        IHostApplicationLifetime lifetime,
        ILogger<DefaultBinSeedJobService> logger)
    {
        _sql = sql;
        _sap = sap;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>Latest job (running or finished), or null when none has run since startup.</summary>
    public DefaultBinSeedJob? Current => _current;

    /// <summary>
    /// Dry run: what a live run with these settings would do — no writes, returns
    /// immediately (one SQL query).
    /// </summary>
    public async Task<DefaultBinSeedAnalysis> AnalyzeAsync(bool overwrite, CancellationToken ct)
    {
        var rows = await _sql.GetDefaultBinSeedRowsAsync(ct);
        var toWrite = FilterRows(rows, overwrite);

        return new DefaultBinSeedAnalysis
        {
            TotalPairs = rows.Count,
            TotalItems = rows.Select(r => r.ItemCode).Distinct().Count(),
            PairsToSet = toWrite.Count,
            PairsAlreadyCorrect = rows.Count(r => r.CurrentDftBinAbs == r.BinAbs),
            PairsWithDifferentDefault = rows.Count(r =>
                r.CurrentDftBinAbs is > 0 && r.CurrentDftBinAbs != r.BinAbs),
            Sample = toWrite.Take(20).ToList(),
        };
    }

    /// <summary>Start a live seeding run (or return the one already running).</summary>
    public DefaultBinSeedJob Start(bool overwrite, int limit)
    {
        lock (_startLock)
        {
            if (_current is { Status: "running" } running)
                return running;

            var job = new DefaultBinSeedJob
            {
                JobId = Guid.NewGuid(),
                StartedAt = DateTime.UtcNow,
                Overwrite = overwrite,
                Limit = limit,
            };
            _current = job;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            var ct = _cts.Token;
            _ = Task.Run(() => RunAsync(job, ct));
            return job;
        }
    }

    /// <summary>Request a graceful stop of the running job (finishes the current item).</summary>
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

    private async Task RunAsync(DefaultBinSeedJob job, CancellationToken ct)
    {
        var failures = new List<DefaultBinSeedFailure>();
        try
        {
            var rows = await _sql.GetDefaultBinSeedRowsAsync(ct);
            var toWrite = FilterRows(rows, job.Overwrite);

            // One Update per item covering all its warehouses (spec §10).
            var groups = toWrite
                .GroupBy(r => r.ItemCode)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();
            if (job.Limit > 0)
                groups = groups.Take(job.Limit).ToList();

            job.TotalItems = groups.Count;
            _logger.LogInformation(
                "Default-bin seed {JobId} starting: {Items} items / {Pairs} pairs " +
                "(overwrite={Overwrite}, limit={Limit}).",
                job.JobId, groups.Count, toWrite.Count, job.Overwrite, job.Limit);

            foreach (var grp in groups)
            {
                if (ct.IsCancellationRequested)
                {
                    job.Status = "stopped";
                    break;
                }

                try
                {
                    var map = grp.ToDictionary(r => r.WhsCode, r => r.BinAbs);
                    bool updated = await _sap.SetItemDefaultBinsAsync(grp.Key, map, ct);
                    if (updated) job.UpdatedItems++;
                    else job.UnchangedItems++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    job.Status = "stopped";
                    break;
                }
                catch (Exception ex)
                {
                    job.FailedItems++;
                    if (failures.Count < MaxRecordedFailures)
                        failures.Add(new DefaultBinSeedFailure(grp.Key, ex.Message));
                    _logger.LogWarning(ex,
                        "Default-bin seed {JobId}: item {ItemCode} failed.", job.JobId, grp.Key);
                }
                finally
                {
                    job.ProcessedItems++;
                }
            }

            job.Failures = failures;
            job.FinishedAt = DateTime.UtcNow;
            if (job.Status == "running")
                job.Status = "done";

            _logger.LogInformation(
                "Default-bin seed {JobId} {Status}: processed {Processed}/{Total}, " +
                "updated {Updated}, unchanged {Unchanged}, failed {Failed}.",
                job.JobId, job.Status, job.ProcessedItems, job.TotalItems,
                job.UpdatedItems, job.UnchangedItems, job.FailedItems);
        }
        catch (Exception ex)
        {
            job.Failures = failures;
            job.Error = ex.Message;
            job.FinishedAt = DateTime.UtcNow;
            job.Status = "failed";
            _logger.LogError(ex, "Default-bin seed {JobId} failed.", job.JobId);
        }
    }

    /// <summary>Rows a run would write: empty defaults, plus wrong defaults when overwriting.</summary>
    private static List<DefaultBinSeedRow> FilterRows(List<DefaultBinSeedRow> rows, bool overwrite) =>
        rows.Where(r => r.CurrentDftBinAbs != r.BinAbs
                        && (overwrite || r.CurrentDftBinAbs is null or <= 0))
            .ToList();
}
