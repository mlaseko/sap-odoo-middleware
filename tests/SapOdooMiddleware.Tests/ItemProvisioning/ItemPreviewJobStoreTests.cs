using SapOdooMiddleware.ItemProvisioning;

namespace SapOdooMiddleware.Tests.ItemProvisioning;

public class ItemPreviewJobStoreTests
{
    [Fact]
    public void SameCodeWhileQueued_SharesOneJob()
    {
        var store = new ItemPreviewJobStore();
        var a = store.Enqueue(new LubesItemPreviewRequest("21130"));
        var b = store.Enqueue(new LubesItemPreviewRequest(" 21130 "));
        Assert.Equal(a, b);
    }

    [Fact]
    public void OtherCodeOrBrand_GetsItsOwnJob()
    {
        var store = new ItemPreviewJobStore();
        var a = store.Enqueue(new LubesItemPreviewRequest("21130"));
        Assert.NotEqual(a, store.Enqueue(new LubesItemPreviewRequest("23139")));
        Assert.NotEqual(a, store.Enqueue(new LubesItemPreviewRequest("21130", "meguin")));
    }

    [Fact]
    public void CompletedRecently_IsReused_FailedIsNot()
    {
        var store = new ItemPreviewJobStore();
        var done = store.Enqueue(new LubesItemPreviewRequest("21130"));
        store.Complete(done, new LubesItemPreviewResult("found", "21130"));
        Assert.Equal(done, store.Enqueue(new LubesItemPreviewRequest("21130")));

        var failed = store.Enqueue(new LubesItemPreviewRequest("23139"));
        store.Fail(failed, "boom");
        Assert.NotEqual(failed, store.Enqueue(new LubesItemPreviewRequest("23139")));
    }

    [Fact]
    public void Snapshot_TracksTheLifecycle()
    {
        var store = new ItemPreviewJobStore();
        var id = store.Enqueue(new LubesItemPreviewRequest("21130", "liqui_moly"));
        Assert.Equal("queued", store.GetSnapshot(id)!.Status);
        store.MarkRunning(id);
        Assert.Equal("running", store.GetSnapshot(id)!.Status);
        store.Complete(id, new LubesItemPreviewResult("not_found", "21130"));
        var snap = store.GetSnapshot(id)!;
        Assert.Equal("completed", snap.Status);
        Assert.Equal("not_found", snap.Result!.Status);
        Assert.Null(store.GetSnapshot("nope"));
    }

    [Fact]
    public void ScrapeOnlyAndClassified_AreSeparateJobs()
    {
        var store = new ItemPreviewJobStore();
        var scrape = store.Enqueue(new LubesItemPreviewRequest("21130", Classify: false));
        Assert.NotEqual(scrape, store.Enqueue(new LubesItemPreviewRequest("21130", Classify: true)));
        Assert.False(store.GetSnapshot(scrape)!.Classify);
    }

    [Fact]
    public void Progress_KeepsThePlanInOrder_WithTimesAndTheItem()
    {
        var store = new ItemPreviewJobStore();
        var id = store.Enqueue(new LubesItemPreviewRequest("21130"));
        var p = store.ProgressFor(id);
        p.Step("lookup", "Look", "pending");
        p.Step("scrape", "Read", "pending");
        p.Step("lookup", "Look", "running");
        p.Step("lookup", "Look", "done", "not fetched before");
        p.Step("index", "List", "running", "warming");   // goes before the page read
        p.Product(new MolasLubes.Infrastructure.Integrations.LiquiMoly.LiquiMolyProductDto
            { ArticleNumber = "21130", Name = "Coolant" }, "Liqui Moly", "21130-Coolant-1 l");

        var snap = store.GetSnapshot(id)!;
        Assert.Equal(new[] { "lookup", "index", "scrape" }, snap.Steps.Select(s => s.Key));
        Assert.Equal("done", snap.Steps[0].State);
        Assert.NotNull(snap.Steps[0].Seconds);
        Assert.Equal("21130-Coolant-1 l", snap.ItemName);

        store.Fail(id, "boom");
        snap = store.GetSnapshot(id)!;
        Assert.Equal("failed", snap.Steps[1].State);
        Assert.Equal("not run", snap.Steps[2].Detail);
    }

    [Fact]
    public void QueueAhead_CountsRunningAndEarlierQueued()
    {
        var store = new ItemPreviewJobStore();
        var a = store.Enqueue(new LubesItemPreviewRequest("1001"));
        var b = store.Enqueue(new LubesItemPreviewRequest("1002"));
        var c = store.Enqueue(new LubesItemPreviewRequest("1003"));
        store.MarkRunning(a);
        Assert.Equal(0, store.GetSnapshot(a)!.QueueAhead);
        Assert.Equal(1, store.GetSnapshot(b)!.QueueAhead);
        Assert.Equal(2, store.GetSnapshot(c)!.QueueAhead);
    }
}
