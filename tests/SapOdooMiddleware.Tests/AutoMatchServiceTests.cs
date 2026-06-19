using Microsoft.Extensions.Logging.Abstractions;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

public class AutoMatchServiceTests
{
    private sealed class FakeOitm : IOitmMatchRepository
    {
        public OitmMatch? ByOem;
        public OitmMatch? ByArticle;
        public IReadOnlyList<string>? LastOems;
        public string? LastArticle;

        public Task<OitmMatch?> FindByOemAsync(IReadOnlyList<string> oems, CancellationToken ct)
        {
            LastOems = oems;
            return Task.FromResult(ByOem);
        }
        public Task<OitmMatch?> FindByArticleAsync(string article, CancellationToken ct)
        {
            LastArticle = article;
            return Task.FromResult(ByArticle);
        }
    }

    private static OitmMatch Oem(string code, string? supplier) => new(code, 1, supplier, "cross_ref_oem");
    private static OitmMatch Art(string code, string? supplier) => new(code, 2, supplier, "article_number");

    private static PartsLineMatchCandidate Line(
        IEnumerable<string>? oems = null, string? article = null, bool promo = false, string? brand = null,
        string? docSupplier = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), (oems ?? Array.Empty<string>()).ToList(), article, promo, brand, docSupplier);

    private static AutoMatchService Build(FakeOitm oitm) => new(oitm, new OemFilterService(), NullLogger<AutoMatchService>.Instance);

    [Fact]
    public async Task Promotional_IsSkipped()
    {
        var d = await Build(new FakeOitm()).DecideAsync(Line(promo: true), CancellationToken.None);
        Assert.Equal("skip", d.Status);
    }

    [Fact]
    public async Task Tier1_OemHit_SameSupplier_Matches()
    {
        var oitm = new FakeOitm { ByOem = Oem("LR100126", "vika") };
        var d = await Build(oitm).DecideAsync(Line(oems: new[] { "LR029078", "Front Right" }, article: "GL0569", brand: "vika"), CancellationToken.None);

        Assert.Equal("matched", d.Status);
        Assert.Equal("LR100126", d.ItemCode);
        Assert.Equal("tier1_oem", d.MatchStrategy);
        Assert.Equal(new[] { "LR029078" }, oitm.LastOems);   // noise filtered before lookup
    }

    [Fact]
    public async Task Tier1_OemHit_DifferentSupplier_DoesNotMatch_FallsThrough()
    {
        // OEM hit under DPA but invoice brand vika → must NOT auto-match; no article hit → pending.
        var oitm = new FakeOitm { ByOem = Oem("BM12850", "DPA") };
        var d = await Build(oitm).DecideAsync(Line(oems: new[] { "33556764428" }, article: "X-456", brand: "vika"), CancellationToken.None);

        Assert.Equal("pending", d.Status);
        Assert.Null(d.ItemCode);
    }

    [Fact]
    public async Task Tier1_OemHit_VehicleGroupBrand_NeedsConfirmation()
    {
        var oitm = new FakeOitm { ByOem = Oem("VAG11941", "Borsehung") };
        var d = await Build(oitm).DecideAsync(Line(oems: new[] { "06J109259A" }, article: "06J109259A", brand: "VAG"), CancellationToken.None);

        Assert.Equal("needs_confirmation", d.Status);
        Assert.Equal("vehicle_group_brand_needs_confirmation", d.MatchStrategy);
        Assert.Equal("VAG11941", d.SuggestedDonor?.ItemCode);
        Assert.Equal("Borsehung", d.SuggestedDonor?.SupplierName);
    }

    [Fact]
    public async Task Tier2_ArticleHit_NullSupplier_Matches_WhenNoOemHit()
    {
        // Exact article match (germax, no supplier) is authoritative identity → matched.
        var oitm = new FakeOitm { ByOem = null, ByArticle = Art("VAG10001", null) };
        var d = await Build(oitm).DecideAsync(Line(oems: new[] { "RNB501400" }, article: "G2261", brand: "VAG"), CancellationToken.None);

        Assert.Equal("matched", d.Status);
        Assert.Equal("VAG10001", d.ItemCode);
        Assert.Equal("tier2_article", d.MatchStrategy);
        Assert.Equal("G2261", oitm.LastArticle);
    }

    [Fact]
    public async Task NoHit_StaysPending()
    {
        var d = await Build(new FakeOitm()).DecideAsync(Line(oems: new[] { "RNB501400" }, article: "G2261", brand: "vika"), CancellationToken.None);
        Assert.Equal("pending", d.Status);
        Assert.Null(d.ItemCode);
    }

    [Fact]
    public async Task NoiseOnlyOems_FallsThroughToArticleTier()
    {
        var oitm = new FakeOitm { ByArticle = Art("LR100200", null) };
        var d = await Build(oitm).DecideAsync(Line(oems: new[] { "Rear", "3.0L" }, article: "GL0010", brand: "vika"), CancellationToken.None);

        Assert.Equal("matched", d.Status);
        Assert.Equal("LR100200", d.ItemCode);
        Assert.Equal("GL0010", oitm.LastArticle);
    }

    // ---- Document-supplier brand fallback (blank line.Brand) ----

    [Fact]
    public async Task BlankBrand_DocSupplier_DifferentSupplierDonor_FallsThroughTier1()
    {
        // GJ0198: line.Brand blank, document supplier 'Germax'; Tier-1 OEM donor is an OE-supplier item.
        // Effective brand 'Germax' ≠ 'OE' → DifferentSupplier → fall through Tier 1 (NOT needs_confirmation).
        var oitm = new FakeOitm { ByOem = Oem("LR100602", "OE"), ByArticle = null };
        var d = await Build(oitm).DecideAsync(
            Line(oems: new[] { "LR097157", "LR029146" }, article: "GJ0198", brand: "", docSupplier: "Germax"),
            CancellationToken.None);

        Assert.Equal("pending", d.Status);          // fell through, no Tier-2 hit
        Assert.Null(d.ItemCode);
    }

    [Fact]
    public async Task BlankBrand_DocSupplier_Tier2SameSupplier_Matches()
    {
        // Same line, but Tier 2 now finds the Germax-supplier variant → matched via tier2_article.
        var oitm = new FakeOitm { ByOem = Oem("LR100602", "OE"), ByArticle = Art("LR100746", "GERMAX") };
        var d = await Build(oitm).DecideAsync(
            Line(oems: new[] { "LR097157", "LR029146" }, article: "GJ0198", brand: "", docSupplier: "Germax"),
            CancellationToken.None);

        Assert.Equal("matched", d.Status);
        Assert.Equal("LR100746", d.ItemCode);
        Assert.Equal("tier2_article", d.MatchStrategy);
    }

    [Fact]
    public async Task NonEmptyBrand_StillWins_Tier1SameSupplier_NoRegression()
    {
        // Explicit line.Brand takes precedence; the document supplier is ignored.
        var oitm = new FakeOitm { ByOem = Oem("X100", "BOSCH") };
        var d = await Build(oitm).DecideAsync(
            Line(oems: new[] { "0986452041" }, article: "A1", brand: "BOSCH", docSupplier: "Germax"),
            CancellationToken.None);

        Assert.Equal("matched", d.Status);
        Assert.Equal("X100", d.ItemCode);
        Assert.Equal("tier1_oem", d.MatchStrategy);
    }

    [Fact]
    public async Task BlankBrand_NoDocSupplier_StillNeedsConfirmation_NoRegression()
    {
        // Both blank → NoBrandOnInvoice → needs_confirmation off the OEM donor (unchanged behaviour).
        var oitm = new FakeOitm { ByOem = Oem("LR100602", "OE") };
        var d = await Build(oitm).DecideAsync(
            Line(oems: new[] { "LR097157" }, article: "GJ0198", brand: "", docSupplier: null),
            CancellationToken.None);

        Assert.Equal("needs_confirmation", d.Status);
        Assert.Equal("LR100602", d.SuggestedDonor?.ItemCode);
    }
}
