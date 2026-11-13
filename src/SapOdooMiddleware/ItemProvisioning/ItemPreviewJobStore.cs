using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SapOdooMiddleware.ItemProvisioning;

/// <summary>Pollable snapshot of an inquiry preview job.</summary>
public record ItemPreviewJobSnapshot(
    string JobId,
    string ArticleNumber,
    string? Brand,
    string Status,                       // "queued" | "running" | "completed" | "failed"
    LubesItemPreviewResult? Result,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// In-memory queue + state for inquiry previews (POST /api/items/preview). Kept apart from the provisioning queue
/// so a sales lookup never waits behind an SAP item creation. A request for an article that already has a live or
/// recent (<see cref="Reuse"/>) job returns that job, so a user clicking twice, or two users looking up the same
/// code, share one scrape. Jobs do not survive a restart; the caller simply asks again.
/// </summary>
public interface IItemPreviewJobStore
{
    ChannelReader<string> Reader { get; }
    string Enqueue(LubesItemPreviewRequest request);
    LubesItemPreviewRequest? GetRequest(string jobId);
    void MarkRunning(string jobId);
    void Complete(string jobId, LubesItemPreviewResult result);
    void Fail(string jobId, string error);
    ItemPreviewJobSnapshot? GetSnapshot(string jobId);
}

public sealed class ItemPreviewJobStore : IItemPreviewJobStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(2);
    internal static readonly TimeSpan Reuse = TimeSpan.FromMinutes(10);

    private sealed class Entry
    {
        public required string JobId;
        public required LubesItemPreviewRequest Request;
        public required string Key;
        public string Status = "queued";
        public LubesItemPreviewResult? Result;
        public string? Error;
        public DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? CompletedAt;
    }

    private readonly ConcurrentDictionary<string, Entry> _jobs = new();
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly object _gate = new();

    public ChannelReader<string> Reader => _queue.Reader;

    internal static string KeyOf(LubesItemPreviewRequest r) =>
        $"{(r.ArticleNumber ?? "").Trim().ToUpperInvariant()}|{(r.Brand ?? "").Trim().ToLowerInvariant()}";

    public string Enqueue(LubesItemPreviewRequest request)
    {
        var key = KeyOf(request);
        lock (_gate)
        {
            PruneExpired();
            var now = DateTimeOffset.UtcNow;
            var reusable = _jobs.Values
                .Where(e => e.Key == key && e.Status != "failed"
                            && (e.CompletedAt is null || now - e.CompletedAt < Reuse))
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefault();
            if (reusable is not null)
                return reusable.JobId;

            var jobId = Guid.NewGuid().ToString("N");
            _jobs[jobId] = new Entry { JobId = jobId, Request = request, Key = key };
            _queue.Writer.TryWrite(jobId);
            return jobId;
        }
    }

    public LubesItemPreviewRequest? GetRequest(string jobId)
        => _jobs.TryGetValue(jobId, out var e) ? e.Request : null;

    public void MarkRunning(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var e)) return;
        lock (_gate) { e.Status = "running"; e.StartedAt = DateTimeOffset.UtcNow; }
    }

    public void Complete(string jobId, LubesItemPreviewResult result)
    {
        if (!_jobs.TryGetValue(jobId, out var e)) return;
        lock (_gate) { e.Result = result; e.Status = "completed"; e.CompletedAt = DateTimeOffset.UtcNow; }
    }

    public void Fail(string jobId, string error)
    {
        if (!_jobs.TryGetValue(jobId, out var e)) return;
        lock (_gate) { e.Error = error; e.Status = "failed"; e.CompletedAt = DateTimeOffset.UtcNow; }
    }

    public ItemPreviewJobSnapshot? GetSnapshot(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var e)) return null;
        lock (_gate)
        {
            return new ItemPreviewJobSnapshot(
                e.JobId, e.Request.ArticleNumber, e.Request.Brand, e.Status, e.Result, e.Error,
                e.CreatedAt, e.StartedAt, e.CompletedAt);
        }
    }

    /// <summary>Drop finished jobs older than the retention window (called under the gate).</summary>
    private void PruneExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var kvp in _jobs)
            if (kvp.Value.CompletedAt is { } done && done < cutoff)
                _jobs.TryRemove(kvp.Key, out _);
    }
}
