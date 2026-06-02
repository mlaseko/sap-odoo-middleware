using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

public class AutoMatchServiceTests
{
    private sealed class FakeOitm : IOitmMatchRepository
    {
        public string? ByOem;
        public string? ByArticle;
        public IReadOnlyList<string>? LastOems;
        public string? LastArticle;

        public Task<string?> FindItemCodeByOemAsync(IReadOnlyList<string> oems, CancellationToken ct)
        {
            LastOems = oems;
            return Task.FromResult(ByOem);
        }
        public Task<string?> FindItemCodeByArticleAsync(string article, CancellationToken ct)
        {
            LastArticle = article;
            return Task.FromResult(ByArticle);
        }
    }

    private static PartsLineMatchCandidate Line(
        IEnumerable<string>? oems = null, string? article = null, bool promo = false)
        => new(Guid.NewGuid(), Guid.NewGuid(), (oems ?? Array.Empty<string>()).ToList(), article, promo);

    private static AutoMatchService Build(FakeOitm oitm) => new(oitm, new OemFilterService());

    [Fact]
    public async Task Promotional_IsSkipped()
    {
        var d = await Build(new FakeOitm()).DecideAsync(Line(promo: true), CancellationToken.None);
        Assert.Equal("skip", d.Status);
    }

    [Fact]
    public async Task Tier1_OemHit_Matches()
    {
        var oitm = new FakeOitm { ByOem = "LR100126" };
        var d = await Build(oitm).DecideAsync(Line(oems: new[] { "LR029078", "Front Right" }, article: "GL0569"), CancellationToken.None);

        Assert.Equal("matched", d.Status);
        Assert.Equal("LR100126", d.ItemCode);
        Assert.Equal(new[] { "LR029078" }, oitm.LastOems);   // noise filtered before lookup
    }

    [Fact]
    public async Task Tier2_ArticleHit_Matches_WhenNoOemHit()
    {
        var oitm = new FakeOitm { ByOem = null, ByArticle = "VAG10001" };
        var d = await Build(oitm).DecideAsync(Line(oems: new[] { "RNB501400" }, article: "G2261"), CancellationToken.None);

        Assert.Equal("matched", d.Status);
        Assert.Equal("VAG10001", d.ItemCode);
        Assert.Equal("G2261", oitm.LastArticle);
    }

    [Fact]
    public async Task NoHit_StaysPending()
    {
        var d = await Build(new FakeOitm()).DecideAsync(Line(oems: new[] { "RNB501400" }, article: "G2261"), CancellationToken.None);
        Assert.Equal("pending", d.Status);
        Assert.Null(d.ItemCode);
    }

    [Fact]
    public async Task NoiseOnlyOems_FallsThroughToArticleTier()
    {
        var oitm = new FakeOitm { ByArticle = "LR100200" };
        var d = await Build(oitm).DecideAsync(Line(oems: new[] { "Rear", "3.0L" }, article: "GL0010"), CancellationToken.None);

        Assert.Equal("matched", d.Status);
        Assert.Equal("LR100200", d.ItemCode);
        Assert.Equal("GL0010", oitm.LastArticle);
    }
}
