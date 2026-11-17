using Microsoft.Extensions.Options;
using Moq;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Price review step 3: related parts are split into same part + same tier (compared) and other tiers
/// (reference), priced from SAP, and the line's PL03 is placed against their median.
/// </summary>
public class PriceReviewComparablesServiceTests
{
    private static readonly Guid DocId = Guid.NewGuid();
    private static readonly Guid LineId = Guid.NewGuid();

    private static readonly Dictionary<string, string> Tiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VIKA"] = "economy", ["FEBI BILSTEIN"] = "branded", ["LAND ROVER"] = "genuine", ["DPA"] = "economy",
    };

    private static PartsReviewLineRow Line(string? brand = "VIKA", Guid? docId = null) =>
        new(Id: LineId, DocumentId: docId ?? DocId, LineNumber: 1, PageNumber: 1,
            SupplierArticleNumber: "11221572101", OemNumbers: new List<string> { "LR110708 lr-079.950", "12" },
            Description: "Water pump", Brand: brand,
            Quantity: 1m, Unit: "PC", UnitPriceForeign: 10m, DiscountPct: null, LineTotalForeign: null,
            IsPromotional: false, ReviewStatus: "create_new", MatchedItemCode: null, GeneratedItemCode: null,
            EnrichmentSource: null, BorrowedFromArticle: null, EnrichmentConfirmedAt: null, CreateErrorMessage: null,
            MatchStrategy: null, BorrowedFromSupplier: null,
            SuggestedDonorItemCode: null, SuggestedDonorOitmId: null, SuggestedDonorSupplier: null,
            NeedsReview: false, SelectedComponentVerdict: null, SelectedName: null, SelectedSupplier: null,
            HasDonorCandidates: false, NeedsMarque: false);

    private static PriceReviewLine ReviewLine(decimal formulaPl03, decimal cif, PriceReviewDecisionView? decision = null) =>
        new(LineId, 1, "11221572101", "Water pump", "VIKA", 1m, 10m, null, 10m,
            cif, cif * 1.25m, formulaPl03, null, null, null, null, false, null, null,
            Array.Empty<PriceReviewFlag>(), null, decision);

    private static PartsComparableCandidate Cand(string code, string? part = "water pump", string? brand = "VIKA") =>
        new(OitmId: code.GetHashCode(), ItemCode: code, Name: $"neon {code}", ItemName: $"mirror {code}",
            PartComponent: part, PartGroup: "Cooling", Brand: brand, ArticleNumber: $"A-{code}",
            SupplierName: "Supplier", ImageUrl: $"https://img/{code}.jpg", VehiclesCount: 12,
            OnHand: 3m, Available: 2m,
            SharedOems: new[] { "LR110708" }, Via: new[] { "oem_xref" });

    // SAP supplies prices only; a brand argument is accepted for readability but SAP's brand is not used.
    private static ComparableSapItem Sap(string code, decimal? pl03, string? brand = null, decimal? cost = 20000m,
        bool active = true) =>
        new(code, cost, pl03, pl03 is null ? null : pl03 * 0.8m, active);

    private static PriceReviewComparablesService Service(
        IReadOnlyList<PartsComparableCandidate> candidates, IReadOnlyList<ComparableSapItem> sapItems,
        PriceReviewLine? reviewLine, string? partComponent = "Water Pump", PartsReviewLineRow? line = null,
        Action<IReadOnlyList<string>>? onTokens = null)
    {
        var lines = new Mock<IPartsReviewRepository>();
        lines.Setup(r => r.GetByIdAsync(LineId, It.IsAny<CancellationToken>())).ReturnsAsync(line ?? Line());

        var repo = new Mock<IPartsComparablesRepository>();
        repo.Setup(r => r.GetLineAnchorsAsync(DocId, LineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PartsLineAnchors(LineId, new long[] { 7 }, Array.Empty<string>(), partComponent));
        repo.Setup(r => r.FindCandidatesAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<long> _, IReadOnlyList<string> toks, IReadOnlyList<string> _, int _, CancellationToken _) =>
                onTokens?.Invoke(toks))
            .ReturnsAsync(candidates);
        repo.Setup(r => r.GetBrandTiersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Tiers);

        var sap = new Mock<IAutohubInventorySqlService>();
        sap.Setup(s => s.GetComparableItemsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sapItems.ToDictionary(i => i.ItemCode, StringComparer.OrdinalIgnoreCase));

        var review = new Mock<IPriceReviewService>();
        review.Setup(r => r.GetReviewAsync(DocId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PriceReviewDocument(DocId, "USD", 2700m, null,
                new PriceReviewSummary(1, 0, 0, 0, 1, 0, 0),
                reviewLine is null ? Array.Empty<PriceReviewLine>() : new[] { reviewLine }));

        return new PriceReviewComparablesService(review.Object, lines.Object, repo.Object, sap.Object,
            Options.Create(new AutohubPricingSettings()));
    }

    [Fact]
    public async Task Splits_by_part_and_tier_and_prices_from_SAP()
    {
        var svc = Service(
            new[]
            {
                Cand("VK1"), Cand("VK2"), Cand("VK3"),
                Cand("FB1", brand: "FEBI BILSTEIN"),           // same part, other tier
                Cand("TH1", part: "Thermostat"),                // other part: counted only
                Cand("GONE"),                                   // not in SAP
                Cand("FROZEN"),                                 // inactive in SAP
            },
            new[]
            {
                Sap("VK1", 100000m), Sap("VK2", 120000m), Sap("VK3", null),
                Sap("FB1", 300000m, brand: "FEBI BILSTEIN"), Sap("TH1", 50000m), Sap("FROZEN", 1m, active: false),
            },
            ReviewLine(formulaPl03: 150000m, cif: 27000m));

        var r = await svc.GetAsync(DocId, LineId, CancellationToken.None);

        Assert.Equal(new[] { "VK1", "VK2", "VK3" }, r!.Same.Select(p => p.ItemCode));
        Assert.Equal("FB1", Assert.Single(r.OtherTier).ItemCode);
        Assert.Equal(1, r.OtherPartCount);
        Assert.Equal("economy", r.Tier);
        Assert.True(r.PartNameKnown);
        Assert.Equal(new ComparableStats(2, 100000m, 110000m, 120000m), r.SameStats);   // VK3 has no PL03
        Assert.Equal(150000m, r.LinePl03);
        Assert.Equal("calculated", r.LinePl03Source);
        Assert.Equal(0.3636m, r.VsMedian);                                               // 150k / 110k − 1
        Assert.Equal("above", r.Position);
        Assert.Contains(r.Flags, f => f.Code == "above_related");
        // Details from Neon, prices from SAP.
        var vk1 = r.Same[0];
        Assert.Equal("neon VK1", vk1.Name);
        Assert.Equal("A-VK1", vk1.ArticleNumber);
        Assert.Equal("https://img/VK1.jpg", vk1.ImageUrl);
        Assert.Equal(3m, vk1.OnHand);
        Assert.Equal(100000m, vk1.Pl03);
        Assert.Equal(80000m, vk1.Pl05);
    }

    [Fact]
    public async Task A_current_decision_is_the_price_compared()
    {
        var decision = new PriceReviewDecisionView("approved", 110000m, 90000m, 0.5m, "match market", "rev",
            DateTime.UtcNow, Stale: false, Array.Empty<string>());
        var svc = Service(new[] { Cand("VK1"), Cand("VK2") }, new[] { Sap("VK1", 100000m), Sap("VK2", 120000m) },
            ReviewLine(formulaPl03: 150000m, cif: 27000m, decision));

        var r = await svc.GetAsync(DocId, LineId, CancellationToken.None);

        Assert.Equal(110000m, r!.LinePl03);
        Assert.Equal("decision", r.LinePl03Source);
        Assert.Equal(0m, r.VsMedian);
        Assert.Equal("in_line", r.Position);
        Assert.Empty(r.Flags);
    }

    [Fact]
    public async Task Unknown_part_name_splits_by_tier_only()
    {
        var svc = Service(new[] { Cand("VK1", part: "Thermostat"), Cand("FB1", brand: "FEBI BILSTEIN") },
            new[] { Sap("VK1", 1000m), Sap("FB1", 2000m, brand: "FEBI BILSTEIN") },
            ReviewLine(150000m, 27000m), partComponent: null);

        var r = await svc.GetAsync(DocId, LineId, CancellationToken.None);

        Assert.False(r!.PartNameKnown);
        Assert.Equal("VK1", Assert.Single(r.Same).ItemCode);
        Assert.Equal("FB1", Assert.Single(r.OtherTier).ItemCode);
        Assert.Equal(0, r.OtherPartCount);
    }

    [Fact]
    public async Task Invoice_cost_far_above_the_same_part_average_cost_is_flagged()
    {
        var svc = Service(new[] { Cand("VK1"), Cand("VK2"), Cand("VK3") },
            new[] { Sap("VK1", 100000m, cost: 8000m), Sap("VK2", 100000m, cost: 9000m), Sap("VK3", 100000m, cost: 10000m) },
            ReviewLine(formulaPl03: 100000m, cif: 30000m));

        var r = await svc.GetAsync(DocId, LineId, CancellationToken.None);

        Assert.Equal(9000m, r!.MedianAvgCost);
        Assert.Contains(r.Flags, f => f.Code == "cost_outlier");
    }

    [Fact]
    public async Task Line_OEM_cells_are_split_and_normalised_for_the_lookup()
    {
        IReadOnlyList<string>? sent = null;
        var svc = Service(Array.Empty<PartsComparableCandidate>(), Array.Empty<ComparableSapItem>(),
            ReviewLine(1m, 1m), onTokens: t => sent = t);

        var r = await svc.GetAsync(DocId, LineId, CancellationToken.None);

        Assert.Equal(new[] { "LR079950", "LR110708" }, sent);   // "12" is too short
        Assert.Equal("No related parts found in SAP for this line.", r!.Note);
        Assert.Null(r.SameStats);
        Assert.Null(r.Position);
    }

    [Fact]
    public async Task Line_on_another_document_is_not_found()
    {
        var svc = Service(Array.Empty<PartsComparableCandidate>(), Array.Empty<ComparableSapItem>(), null,
            line: Line(docId: Guid.NewGuid()));

        Assert.Null(await svc.GetAsync(DocId, LineId, CancellationToken.None));
    }

    [Theory]
    [InlineData("vika", "economy", true)]
    [InlineData("  Febi   Bilstein ", "branded", true)]
    [InlineData("VIKA DPA", "economy", true)]          // first word decides
    [InlineData("Land Rover", "genuine", true)]
    [InlineData("ACME", "economy", false)]             // unknown: economy, not mapped
    [InlineData(null, "economy", false)]
    public void Tier_of_a_brand(string? brand, string tier, bool mapped)
    {
        Assert.Equal((tier, mapped), PriceReviewComparablesService.TierOf(brand, Tiers));
    }

    // ── Batch: every line when the page opens ──

    [Fact]
    public async Task Batch_returns_every_review_line_pricing_the_review_and_SAP_once()
    {
        var second = Guid.NewGuid();
        var lines = new Mock<IPartsReviewRepository>();
        lines.Setup(r => r.ListByDocumentAsync(DocId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new[] { Line(), Line() with { Id = second, LineNumber = 2 }, Line() with { Id = Guid.NewGuid(), LineNumber = 3 } });

        var repo = new Mock<IPartsComparablesRepository>();
        repo.Setup(r => r.GetLineAnchorsAsync(DocId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid id, CancellationToken _) => new PartsLineAnchors(id, new long[] { 7 }, Array.Empty<string>(), "Water Pump"));
        repo.Setup(r => r.FindCandidatesAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Cand("VK1"), Cand("VK2"), Cand("FB1", brand: "FEBI BILSTEIN") });
        repo.Setup(r => r.GetBrandTiersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Tiers);

        var sap = new Mock<IAutohubInventorySqlService>();
        sap.Setup(s => s.GetComparableItemsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new[] { Sap("VK1", 100000m), Sap("VK2", 120000m), Sap("FB1", 300000m) }
               .ToDictionary(i => i.ItemCode, StringComparer.OrdinalIgnoreCase));

        // Lines 1 and 2 are in the review; line 3 is not (e.g. already created) and is left out.
        var review = new Mock<IPriceReviewService>();
        review.Setup(r => r.GetReviewAsync(DocId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PriceReviewDocument(DocId, "USD", 2700m, null, new PriceReviewSummary(2, 0, 0, 0, 2, 0, 0),
                  new[] { ReviewLine(110000m, 30000m), ReviewLine(150000m, 30000m) with { LineId = second, LineNumber = 2 } }));

        var svc = new PriceReviewComparablesService(review.Object, lines.Object, repo.Object, sap.Object,
            Options.Create(new AutohubPricingSettings()));
        var batch = (await svc.GetForDocumentAsync(DocId, CancellationToken.None))!;

        Assert.Empty(batch.Errors);
        Assert.Equal(new[] { LineId, second }, batch.Lines.Select(l => l.LineId));
        Assert.All(batch.Lines, l => { Assert.Equal(2, l.Same.Count); Assert.Single(l.OtherTier); Assert.Equal(110000m, l.SameStats!.Median); });
        Assert.Equal(110000m, batch.Lines[0].LinePl03);
        Assert.Equal("in_line", batch.Lines[0].Position);
        Assert.Equal(150000m, batch.Lines[1].LinePl03);
        Assert.Equal("above", batch.Lines[1].Position);
        review.Verify(r => r.GetReviewAsync(DocId, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.GetBrandTiersAsync(It.IsAny<CancellationToken>()), Times.Once);
        sap.Verify(s => s.GetComparableItemsAsync(It.Is<IReadOnlyCollection<string>>(c => c.Count == 3), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Batch_a_line_that_fails_is_reported_and_the_others_still_come_back()
    {
        var second = Guid.NewGuid();
        var lines = new Mock<IPartsReviewRepository>();
        lines.Setup(r => r.ListByDocumentAsync(DocId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new[] { Line(), Line() with { Id = second, LineNumber = 2 } });
        var repo = new Mock<IPartsComparablesRepository>();
        repo.Setup(r => r.GetLineAnchorsAsync(DocId, LineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PartsLineAnchors(LineId, new long[] { 7 }, Array.Empty<string>(), "Water Pump"));
        repo.Setup(r => r.GetLineAnchorsAsync(DocId, second, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("timeout"));
        repo.Setup(r => r.FindCandidatesAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Cand("VK1") });
        repo.Setup(r => r.GetBrandTiersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Tiers);
        var sap = new Mock<IAutohubInventorySqlService>();
        sap.Setup(s => s.GetComparableItemsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new[] { Sap("VK1", 100000m) }.ToDictionary(i => i.ItemCode, StringComparer.OrdinalIgnoreCase));
        var review = new Mock<IPriceReviewService>();
        review.Setup(r => r.GetReviewAsync(DocId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PriceReviewDocument(DocId, "USD", 2700m, null, new PriceReviewSummary(2, 0, 0, 0, 2, 0, 0),
                  new[] { ReviewLine(110000m, 30000m), ReviewLine(110000m, 30000m) with { LineId = second, LineNumber = 2 } }));

        var batch = (await new PriceReviewComparablesService(review.Object, lines.Object, repo.Object, sap.Object,
            Options.Create(new AutohubPricingSettings())).GetForDocumentAsync(DocId, CancellationToken.None))!;

        Assert.Equal(LineId, Assert.Single(batch.Lines).LineId);
        var error = Assert.Single(batch.Errors);
        Assert.Equal(second, error.LineId);
        Assert.Contains("timeout", error.Error);
    }

    [Fact]
    public async Task Batch_unknown_document_is_null()
    {
        var review = new Mock<IPriceReviewService>();
        review.Setup(r => r.GetReviewAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((PriceReviewDocument?)null);
        var svc = new PriceReviewComparablesService(review.Object, new Mock<IPartsReviewRepository>().Object,
            new Mock<IPartsComparablesRepository>().Object, new Mock<IAutohubInventorySqlService>().Object,
            Options.Create(new AutohubPricingSettings()));
        Assert.Null(await svc.GetForDocumentAsync(Guid.NewGuid(), CancellationToken.None));
    }

    // ── SAP's article number wins over a mislinked catalogue row ──

    [Fact]
    public async Task Sap_article_wins_when_the_catalogue_row_describes_another_part_and_same_article_is_flagged()
    {
        // VAG12331 case: the catalogue row carrying the code says B12746 "Timing Chain"; SAP says B12698,
        // which is the invoice line's own article, so the part may already be in SAP.
        var mislinked = Cand("VAG12331") with { Name = "Timing Chain", ItemName = "Oil pump B12698", ArticleNumber = "B12746" };
        var agreeing = Cand("VK2") with { ArticleNumber = "A-VK2" };
        var svc = Service(
            new[] { mislinked, agreeing },
            new[]
            {
                new ComparableSapItem("VAG12331", 20000m, 450000m, 272872m, true, ArticleNumber: "B-12698", ItemName: "06J115105AB/B12698"),
                new ComparableSapItem("VK2", 20000m, 400000m, 320000m, true, ArticleNumber: "A-VK2"),
            },
            ReviewLine(320000m, 84260m),
            line: Line() with { SupplierArticleNumber = "B12698" });

        var r = (await svc.GetAsync(DocId, LineId, CancellationToken.None))!;

        var p = r.Same.Single(x => x.ItemCode == "VAG12331");
        Assert.Equal("B-12698", p.ArticleNumber);
        Assert.Equal("B12746", p.CatalogueArticleNumber);
        Assert.Equal("Oil pump B12698", p.Name);
        Assert.True(p.SameArticle);

        var ok = r.Same.Single(x => x.ItemCode == "VK2");
        Assert.Null(ok.CatalogueArticleNumber);
        Assert.False(ok.SameArticle);
        Assert.Equal("neon VK2", ok.Name);

        var flag = Assert.Single(r.Flags, f => f.Code == "same_article");
        Assert.Contains("VAG12331", flag.Text);
    }

    [Theory]
    [InlineData("B12698", "B-12698", true)]
    [InlineData("b 12698", "B12698", true)]
    [InlineData("B12698", "B12746", false)]
    public void Article_numbers_compare_on_letters_and_digits(string a, string b, bool same) =>
        Assert.Equal(same, PriceReviewComparablesService.NormArticle(a) == PriceReviewComparablesService.NormArticle(b));
}
