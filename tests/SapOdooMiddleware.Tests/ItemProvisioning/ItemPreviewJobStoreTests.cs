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
}
