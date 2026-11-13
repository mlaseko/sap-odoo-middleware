using System.Collections.Concurrent;
using System.Threading.Channels;
using MolasLubes.Infrastructure.Integrations.LiquiMoly;

namespace SapOdooMiddleware.ItemProvisioning;

/// <summary>Pollable snapshot of an inquiry preview job.</summary>
public record ItemPreviewJobSnapshot(
    string JobId,
    string ArticleNumber,
    string? Brand,
    bool Classify,
    string Status,                       // "queued" | "running" | "completed" | "failed"
    int QueueAhead,                      // jobs to run before this one (queued only)
    double ElapsedSeconds,
    List<ItemPreviewStep> Steps,         // the live plan: each step pending / running / done / skipped / failed
    string? ItemName,                    // known as soon as the page is read (before the classification)
    string? ScrapedBrand,
    LiquiMolyProductDto? Product,
    LubesItemPreviewResult? Result,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// In-memory queue + state for inquiry previews (POST /api/items/preview). Kept apart from the provisioning queue
/// so a sales lookup never waits behind an SAP item creation.
///
/// A request for an article (+ brand + classify) that already has a job returns that job, so a user clicking twice,
/// two users looking up the same code, or a user coming back later share one scrape: a running job always, a
/// complete result for <see cref="ReuseComplete"/> (the item does not change in a day), anything else (not found,
/// classifier down) for <see cref="ReuseOther"/>. Jobs do not survive a restart; the caller simply asks again.
/// </summary>
public interface IItemPreviewJobStore
{
    ChannelReader<string> Reader { get; }
    string Enqueue(LubesItemPreviewRequest request);
    LubesItemPreviewRequest? GetRequest(string jobId);
    void MarkRunning(string jobId);
    IItemPreviewProgress ProgressFor(string jobId);
    void Complete(string jobId, LubesItemPreviewResult result);
    void Fail(string jobId, string error);
    ItemPreviewJobSnapshot? GetSnapshot(string jobId);
}

public sealed class ItemPreviewJobStore : IItemPreviewJobStore
{
    internal static readonly TimeSpan ReuseComplete = TimeSpan.FromHours(24);
    internal static readonly TimeSpan ReuseOther = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(25);

    private sealed class Entry
    {
        public required string JobId;
        public required LubesItemPreviewRequest Request;
        public required string Key;
        public string Status = "queued";
        public readonly List<ItemPreviewStep> Steps = new();
        public readonly Dictionary<string, DateTimeOffset> StepStarted = new();
        public string? ItemName;
        public string? ScrapedBrand;
        public LiquiMolyProductDto? Product;
        public LubesItemPreviewResult? Result;
        public string? Error;
        public DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? CompletedAt;
    }

    private sealed class Progress(ItemPreviewJobStore store, string jobId) : IItemPreviewProgress
    {
        public void Step(string key, string label, string state, string? detail = null) =>
            store.SetStep(jobId, key, label, state, detail);

        public void Product(LiquiMolyProductDto product, string brand, string itemName) =>
            store.SetProduct(jobId, product, brand, itemName);
    }

    private readonly ConcurrentDictionary<string, Entry> _jobs = new();
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly object _gate = new();

    public ChannelReader<string> Reader => _queue.Reader;

    internal static string KeyOf(LubesItemPreviewRequest r) =>
        $"{(r.ArticleNumber ?? "").Trim().ToUpperInvariant()}|{(r.Brand ?? "").Trim().ToLowerInvariant()}|{r.Classify}";

    private static bool Reusable(Entry e, DateTimeOffset now)
    {
        if (e.Status == "failed") return false;
        if (e.CompletedAt is not { } done) return true;   // queued or running
        var complete = e.Result is { Status: "found" } r && (!e.Request.Classify || r.ClassificationComplete);
        return now - done < (complete ? ReuseComplete : ReuseOther);
    }

    public string Enqueue(LubesItemPreviewRequest request)
    {
        var key = KeyOf(request);
        lock (_gate)
        {
            PruneExpired();
            var now = DateTimeOffset.UtcNow;
            var reusable = _jobs.Values
                .Where(e => e.Key == key && Reusable(e, now))
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

    public IItemPreviewProgress ProgressFor(string jobId) => new Progress(this, jobId);

    private void SetStep(string jobId, string key, string label, string state, string? detail)
    {
        if (!_jobs.TryGetValue(jobId, out var e)) return;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (state == "running")
                e.StepStarted[key] = now;
            double? seconds = state is "done" or "failed" && e.StepStarted.TryGetValue(key, out var started)
                ? Math.Round((now - started).TotalSeconds, 1)
                : null;
            var step = new ItemPreviewStep(key, label, state, detail, seconds);
            var i = e.Steps.FindIndex(s => s.Key == key);
            if (i >= 0)
                e.Steps[i] = step;
            else if (key == "index" && e.Steps.FindIndex(s => s.Key == "scrape") is var at and >= 0)
                e.Steps.Insert(at, step);   // the product list comes before reading the page
            else
                e.Steps.Add(step);
        }
    }

    private void SetProduct(string jobId, LiquiMolyProductDto product, string brand, string itemName)
    {
        if (!_jobs.TryGetValue(jobId, out var e)) return;
        lock (_gate) { e.Product = product; e.ScrapedBrand = brand; e.ItemName = itemName; }
    }

    public void Complete(string jobId, LubesItemPreviewResult result)
    {
        if (!_jobs.TryGetValue(jobId, out var e)) return;
        lock (_gate) { e.Result = result; e.Status = "completed"; e.CompletedAt = DateTimeOffset.UtcNow; }
    }

    public void Fail(string jobId, string error)
    {
        if (!_jobs.TryGetValue(jobId, out var e)) return;
        lock (_gate)
        {
            e.Error = error; e.Status = "failed"; e.CompletedAt = DateTimeOffset.UtcNow;
            for (var i = 0; i < e.Steps.Count; i++)
                if (e.Steps[i].State is "running" or "pending")
                    e.Steps[i] = e.Steps[i] with { State = "failed", Detail = e.Steps[i].State == "running" ? error : "not run" };
        }
    }

    public ItemPreviewJobSnapshot? GetSnapshot(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var e)) return null;
        lock (_gate)
        {
            var ahead = e.Status == "queued"
                ? _jobs.Values.Count(o => o.Status == "queued" && o.CreatedAt < e.CreatedAt)
                  + _jobs.Values.Count(o => o.Status == "running")
                : 0;
            var end = e.CompletedAt ?? DateTimeOffset.UtcNow;
            return new ItemPreviewJobSnapshot(
                e.JobId, e.Request.ArticleNumber, e.Request.Brand, e.Request.Classify, e.Status, ahead,
                Math.Round((end - e.CreatedAt).TotalSeconds, 1), e.Steps.ToList(), e.ItemName, e.ScrapedBrand,
                e.Product, e.Result, e.Error, e.CreatedAt, e.StartedAt, e.CompletedAt);
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
