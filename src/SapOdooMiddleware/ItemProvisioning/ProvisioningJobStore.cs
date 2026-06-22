using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SapOdooMiddleware.ItemProvisioning;

/// <summary>Pollable snapshot of an async provisioning job.</summary>
public record ProvisioningJobSnapshot(
    string JobId,
    string ArticleNumber,
    string Status,                       // "queued" | "running" | "completed" | "failed"
    LubesProvisioningResult? Result,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// In-process queue + state store for async item provisioning. A single hosted worker
/// drains <see cref="Reader"/> and runs the orchestrator off the request thread, so the
/// HTTP call returns immediately (no Cloudflare ~100s timeout in the path).
/// State is in-memory only — jobs do not survive a restart. That is acceptable because
/// provisioning is idempotent: re-POSTing the same article heals any lost/partial work.
/// </summary>
public interface IProvisioningJobStore
{
    ChannelReader<string> Reader { get; }
    string Enqueue(LubesProvisioningRequest request);
    LubesProvisioningRequest? GetRequest(string jobId);
    void MarkRunning(string jobId);
    void Complete(string jobId, LubesProvisioningResult result);
    void Fail(string jobId, string error);
    ProvisioningJobSnapshot? GetSnapshot(string jobId);
}

public sealed class ProvisioningJobStore : IProvisioningJobStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(6);

    private sealed class Entry
    {
        public required string JobId;
        public required LubesProvisioningRequest Request;
        public string Status = "queued";
        public LubesProvisioningResult? Result;
        public string? Error;
        public DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? CompletedAt;
    }

    private readonly ConcurrentDictionary<string, Entry> _jobs = new();
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _gate = new();

    public ChannelReader<string> Reader => _queue.Reader;

    public string Enqueue(LubesProvisioningRequest request)
    {
        PruneExpired();
        var jobId = Guid.NewGuid().ToString("N");
        _jobs[jobId] = new Entry { JobId = jobId, Request = request };
        _queue.Writer.TryWrite(jobId);
        return jobId;
    }

    public LubesProvisioningRequest? GetRequest(string jobId)
        => _jobs.TryGetValue(jobId, out var e) ? e.Request : null;

    public void MarkRunning(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var e)) return;
        lock (_gate) { e.Status = "running"; e.StartedAt = DateTimeOffset.UtcNow; }
    }

    public void Complete(string jobId, LubesProvisioningResult result)
    {
        if (!_jobs.TryGetValue(jobId, out var e)) return;
        lock (_gate) { e.Result = result; e.Status = "completed"; e.CompletedAt = DateTimeOffset.UtcNow; }
    }

    public void Fail(string jobId, string error)
    {
        if (!_jobs.TryGetValue(jobId, out var e)) return;
        lock (_gate) { e.Error = error; e.Status = "failed"; e.CompletedAt = DateTimeOffset.UtcNow; }
    }

    public ProvisioningJobSnapshot? GetSnapshot(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var e)) return null;
        lock (_gate)
        {
            return new ProvisioningJobSnapshot(
                e.JobId, e.Request.ArticleNumber, e.Status, e.Result, e.Error,
                e.CreatedAt, e.StartedAt, e.CompletedAt);
        }
    }

    /// <summary>Drop finished jobs older than the retention window to bound memory.</summary>
    private void PruneExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var kvp in _jobs)
        {
            var e = kvp.Value;
            if (e.CompletedAt is { } done && done < cutoff)
                _jobs.TryRemove(kvp.Key, out _);
        }
    }
}
