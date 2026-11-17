using Microsoft.Extensions.Options;
using Moq;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Price review step 1: the read-only review must price lines exactly as Bulk Create does
/// (discount before forex, one document rate, batch engine), flag the prices worth a second look,
/// and mark saved decisions stale when the figures behind them change.
/// </summary>
public class PriceReviewServiceTests
{
    private static readonly Guid DocId = Guid.NewGuid();

    // Engine stand-in: PL01 = CIF × 1.25, PL03 = PL01 ÷ 0.5, PL05 = midpoint. Brand "NOBAND" uses DEFAULT.
    private static PricingBatchResult Quote(decimal cif, string? brand)
    {
        var cost = Math.Round(cif * 1.25m, 2);
        var retail = Math.Ceiling(cost / 0.5m);
        var ratioBrand = brand is null or "NOBAND" ? "DEFAULT" : brand;
        return new PricingBatchResult(new PricingQuote(
            Cif: cif, Cost: cost, Retail: retail, Wholesale: Math.Floor(retail - (retail - cost) / 2m),
            Ratio: 0.5m, RatioBrand: ratioBrand, BandMin: 0m, BandMax: null,
            FloorApplied: false, Floor: null, RetailRoundingStep: 1), null);
    }

    private static PartsReviewLineRow Line(
        int n, decimal? unit, string status = "create_new", decimal? discount = null, string? brand = "VIKA") =>
        new(Id: Guid.NewGuid(), DocumentId: DocId, LineNumber: n, PageNumber: 1,
            SupplierArticleNumber: $"ART{n}", OemNumbers: new List<string>(), Description: $"Part {n}", Brand: brand,
            Quantity: 2m, Unit: "PC", UnitPriceForeign: unit, DiscountPct: discount, LineTotalForeign: null,
            IsPromotional: false, ReviewStatus: status, MatchedItemCode: null, GeneratedItemCode: null,
            EnrichmentSource: null, BorrowedFromArticle: null, EnrichmentConfirmedAt: null, CreateErrorMessage: null,
            MatchStrategy: null, BorrowedFromSupplier: null,
            SuggestedDonorItemCode: null, SuggestedDonorOitmId: null, SuggestedDonorSupplier: null,
            NeedsReview: false, SelectedComponentVerdict: null, SelectedName: null, SelectedSupplier: null,
            HasDonorCandidates: false, NeedsMarque: false);

    private static StagingPartsDocumentRow Doc(string? currency) =>
        new(DocId, "inv.pdf", "/x/inv.pdf", "sha", 1, "extracted", "ok", null, DateTime.UtcNow, DateTime.UtcNow,
            1, null, null, "VIKA", "INV-1", DateTime.UtcNow, currency, 100m);

    private static PriceReviewService Service(
        IReadOnlyList<PartsReviewLineRow> lines, string? currency = "USD", decimal? rate = 2700m,
        IReadOnlyDictionary<Guid, PartsPriceDecision>? decisions = null) =>
        Service(lines, out _, currency, rate, decisions);

    private static PriceReviewService Service(
        IReadOnlyList<PartsReviewLineRow> lines, out Mock<IPartsPriceReviewRepository> decisionRepoOut,
        string? currency = "USD", decimal? rate = 2700m,
        IReadOnlyDictionary<Guid, PartsPriceDecision>? decisions = null)
    {
        var docs = new Mock<IStagingPartsDocumentRepository>();
        docs.Setup(d => d.GetByIdAsync(DocId, It.IsAny<CancellationToken>())).ReturnsAsync(Doc(currency));

        var lineRepo = new Mock<IPartsReviewRepository>();
        lineRepo.Setup(r => r.ListByDocumentAsync(DocId, It.IsAny<CancellationToken>())).ReturnsAsync(lines);

        var decisionRepo = new Mock<IPartsPriceReviewRepository>();
        decisionRepo.Setup(r => r.GetByDocumentAsync(DocId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(decisions ?? new Dictionary<Guid, PartsPriceDecision>());
        decisionRepoOut = decisionRepo;

        var forex = new Mock<IForexConversionService>();
        if (rate is { } r)
            forex.Setup(f => f.GetRateAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(r);
        else
            forex.Setup(f => f.GetRateAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("No forex rate configured for USD."));

        var pricing = new Mock<IPricingCalculationService>();
        pricing.Setup(p => p.CalculateBatchDetailedAsync(
                It.IsAny<IReadOnlyList<(decimal SupplierPriceTzs, string? Brand)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(decimal SupplierPriceTzs, string? Brand)> ls, CancellationToken _) =>
                ls.Select(l => Quote(l.SupplierPriceTzs, l.Brand)).ToList());
        pricing.Setup(p => p.DeriveWholesaleAsync(It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal cost, decimal retail, CancellationToken _) => Math.Floor(retail - (retail - cost) / 2m));

        return new PriceReviewService(docs.Object, lineRepo.Object, decisionRepo.Object, forex.Object,
            pricing.Object, Options.Create(new AutohubPricingSettings()));
    }

    [Fact]
    public async Task Only_lines_Bulk_Create_would_create_are_reviewed()
    {
        var lines = new[]
        {
            Line(1, 10m, "create_new"), Line(2, 10m, "matched"), Line(3, 10m, "create_failed"),
            Line(4, 10m, "skip"), Line(5, 10m, "created"),
        };
        var review = await Service(lines).GetReviewAsync(DocId, CancellationToken.None);

        Assert.Equal(new[] { 1, 3 }, review!.Lines.Select(l => l.LineNumber));
    }

    [Fact]
    public async Task Prices_like_Bulk_Create_discount_before_forex()
    {
        var review = await Service(new[] { Line(1, 10m, discount: 10m) }).GetReviewAsync(DocId, CancellationToken.None);
        var l = review!.Lines.Single();

        Assert.Equal(9m, l.EffectiveUnitPrice);          // 10 less 10%
        Assert.Equal(24300m, l.CifTzs);                  // 9 × 2700
        Assert.Equal(30375m, l.Pl01);                    // × 1.25
        Assert.Equal(60750m, l.FormulaPl03);
        Assert.Equal(0.5m, l.FormulaMargin);
        Assert.Null(l.Error);
        Assert.Equal(2700m, review.ForexRate);
    }

    [Fact]
    public async Task Missing_rate_or_price_is_an_error_on_the_line_not_a_failure()
    {
        var noRate = await Service(new[] { Line(1, 10m) }, rate: null).GetReviewAsync(DocId, CancellationToken.None);
        Assert.Contains("No forex rate", noRate!.ForexError);
        Assert.Contains("No forex rate", noRate.Lines.Single().Error);
        Assert.Null(noRate.Lines.Single().FormulaPl03);

        var noCurrency = await Service(new[] { Line(1, 10m) }, currency: " ").GetReviewAsync(DocId, CancellationToken.None);
        Assert.Contains("currency is unknown", noCurrency!.Lines.Single().Error);

        var noPrice = await Service(new[] { Line(1, null), Line(2, 10m) }).GetReviewAsync(DocId, CancellationToken.None);
        Assert.Equal("Line has no positive unit price.", noPrice!.Lines[0].Error);
        Assert.Null(noPrice.Lines[1].Error);
        Assert.Equal(1, noPrice.Summary.Errors);
    }

    [Fact]
    public async Task Brand_without_its_own_ratio_is_flagged()
    {
        var review = await Service(new[] { Line(1, 10m, brand: "NOBAND"), Line(2, 10m, brand: null) })
            .GetReviewAsync(DocId, CancellationToken.None);

        Assert.Contains(review!.Lines[0].Flags, f => f.Code == "no_brand_ratio");
        Assert.DoesNotContain(review.Lines[1].Flags, f => f.Code == "no_brand_ratio"); // no brand: nothing to flag
    }

    [Fact]
    public void Margin_flags_follow_the_settings()
    {
        var svc = Service(Array.Empty<PartsReviewLineRow>());
        var q = Quote(800m, "VIKA").Quote!;   // PL01 1000

        IReadOnlyList<PriceReviewFlag> At(decimal pl03) =>
            svc.FlagsFor(q.Cost, pl03, q.FloorApplied, q.Floor, q.RatioBrand, "VIKA");

        Assert.Contains(At(1000m), f => f.Code == "below_cost");
        Assert.Contains(At(1100m), f => f.Code == "margin_low");     // 9%
        Assert.Empty(At(2000m));                                      // 50%
        Assert.Contains(At(20000m), f => f.Code == "margin_high");   // 95%
    }

    private static PartsPriceDecision Decision(Guid lineId, decimal unit, decimal rate, decimal cif,
        decimal formulaPl03, decimal finalPl03, string status = "approved") =>
        new(lineId, DocId, status, unit, null, "USD", rate, cif, Math.Round(cif * 1.25m, 2), formulaPl03, null,
            finalPl03, null, finalPl03 == formulaPl03 ? null : "market", "rev", DateTime.UtcNow);

    [Fact]
    public async Task Decision_on_the_same_figures_is_current_and_its_PL03_drives_the_flags()
    {
        var line = Line(1, 10m);
        // CIF 27000, PL01 33750, formula PL03 67500; reviewer set 35000 (margin 3.6%).
        var saved = Decision(line.Id, 10m, 2700m, 27000m, 67500m, 35000m);
        var review = await Service(new[] { line }, decisions: new Dictionary<Guid, PartsPriceDecision> { [line.Id] = saved })
            .GetReviewAsync(DocId, CancellationToken.None);
        var l = review!.Lines.Single();

        Assert.False(l.Decision!.Stale);
        Assert.Equal(35000m, l.Decision.FinalPl03);
        Assert.Contains(l.Flags, f => f.Code == "margin_low");
        Assert.Equal(1, review.Summary.Approved);
        Assert.Equal(0, review.Summary.Unreviewed);
    }

    [Fact]
    public async Task Decision_goes_stale_when_the_rate_or_price_changes()
    {
        var line = Line(1, 10m);
        var saved = Decision(line.Id, 10m, 2500m, 25000m, 62500m, 62500m);
        var review = await Service(new[] { line }, decisions: new Dictionary<Guid, PartsPriceDecision> { [line.Id] = saved })
            .GetReviewAsync(DocId, CancellationToken.None);
        var d = review!.Lines.Single().Decision!;

        Assert.True(d.Stale);
        Assert.Contains("Exchange rate changed.", d.StaleReasons);
        Assert.Contains("Cost (CIF) changed.", d.StaleReasons);
        Assert.Equal(1, review.Summary.Stale);
        Assert.Equal(1, review.Summary.Unreviewed);
        Assert.Equal(0, review.Summary.Approved);
    }

    // ── Step 2: live recalculation and decisions ──────────────────────────────

    [Fact]
    public async Task Preview_rederives_PL05_from_the_typed_PL03()
    {
        var line = Line(1, 10m);   // PL01 33750, formula PL03 67500, formula PL05 50625
        var svc = Service(new[] { line });

        var result = await svc.PreviewAsync(DocId, new[]
        {
            new PriceReviewPreviewRequestLine(line.Id, 80000m),
            new PriceReviewPreviewRequestLine(line.Id, null),
            new PriceReviewPreviewRequestLine(Guid.NewGuid(), 1000m),
            new PriceReviewPreviewRequestLine(line.Id, 0m),
        }, CancellationToken.None);
        var typed = result!.Lines[0];

        Assert.Equal(80000m, typed.Pl03);
        Assert.Equal(56875m, typed.Pl05);                 // floor(80000 − (80000 − 33750) / 2)
        Assert.True(typed.ChangedFromFormula);
        Assert.Equal(0.5781m, typed.Margin);
        Assert.Equal(67500m, result.Lines[1].Pl03);
        Assert.False(result.Lines[1].ChangedFromFormula);
        Assert.NotNull(result.Lines[2].Error);            // not on the document
        Assert.Equal("PL03 must be greater than zero.", result.Lines[3].Error);
    }

    [Fact]
    public async Task Approve_saves_recomputed_figures_not_caller_figures()
    {
        var line = Line(1, 10m, discount: 10m);   // CIF 24300, PL01 30375, formula PL03 60750
        var svc = Service(new[] { line }, out var repo);
        IReadOnlyList<PartsPriceDecision>? saved = null;
        repo.Setup(r => r.UpsertAsync(It.IsAny<IReadOnlyList<PartsPriceDecision>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<PartsPriceDecision> d, CancellationToken _) => saved = d)
            .Returns(Task.CompletedTask);

        var result = await svc.DecideAsync(DocId,
            new[] { new PriceReviewDecisionRequestLine(line.Id, "Approved", 70000m, " similar parts at 70k ") },
            "Mohamed", CancellationToken.None);

        Assert.Empty(result!.Errors);
        Assert.Equal(1, result.Saved);
        var d = Assert.Single(saved!);
        Assert.Equal("approved", d.Status);
        Assert.Equal(70000m, d.FinalPl03);
        Assert.Equal(50187m, d.FinalPl05);               // floor(70000 − (70000 − 30375) / 2)
        Assert.Equal(60750m, d.FormulaPl03);
        Assert.Equal(24300m, d.CifTzs);
        Assert.Equal(2700m, d.ForexRate);
        Assert.Equal("USD", d.Currency);
        Assert.Equal(10m, d.DiscountPct);
        Assert.Equal("similar parts at 70k", d.Reason);
        Assert.Equal("Mohamed", d.ReviewedBy);
    }

    [Fact]
    public async Task A_PL03_changed_by_hand_is_saved_with_an_automatic_reason()
    {
        var line = Line(1, 10m);
        var svc = Service(new[] { line }, out var repo);
        IReadOnlyList<PartsPriceDecision>? saved = null;
        repo.Setup(r => r.UpsertAsync(It.IsAny<IReadOnlyList<PartsPriceDecision>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<PartsPriceDecision> d, CancellationToken _) => saved = d)
            .Returns(Task.CompletedTask);

        var result = await svc.DecideAsync(DocId, new[]
        {
            new PriceReviewDecisionRequestLine(line.Id, "approved", 90000m, null),
        }, "rev", CancellationToken.None);

        Assert.Empty(result!.Errors);
        var d = Assert.Single(saved!);
        Assert.Equal(90000m, d.FinalPl03);
        Assert.StartsWith("Set by hand on the Prices tab: ", d.Reason);
        Assert.EndsWith("→ 90,000", d.Reason);
    }

    [Fact]
    public void Automatic_reason_shows_both_prices() =>
        Assert.Equal("Set by hand on the Prices tab: 700,000 → 600,000", PriceReviewService.AutoReason(700000m, 600000m));

    [Fact]
    public async Task Accepting_the_calculated_price_needs_no_reason()
    {
        var line = Line(1, 10m);
        var svc = Service(new[] { line }, out var repo);
        IReadOnlyList<PartsPriceDecision>? saved = null;
        repo.Setup(r => r.UpsertAsync(It.IsAny<IReadOnlyList<PartsPriceDecision>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<PartsPriceDecision> d, CancellationToken _) => saved = d)
            .Returns(Task.CompletedTask);

        var result = await svc.DecideAsync(DocId,
            new[] { new PriceReviewDecisionRequestLine(line.Id, "approved", null, null) }, "rev", CancellationToken.None);

        Assert.Empty(result!.Errors);
        var d = Assert.Single(saved!);
        Assert.Equal(67500m, d.FinalPl03);
        Assert.Equal(50625m, d.FinalPl05);
        Assert.Null(d.Reason);
    }

    [Fact]
    public async Task Any_invalid_line_saves_nothing_and_reports_every_problem()
    {
        var good = Line(1, 10m);
        var noPrice = Line(2, null);
        var other = Line(3, 10m, "matched");
        var svc = Service(new[] { good, noPrice, other }, out var repo);

        var result = await svc.DecideAsync(DocId, new[]
        {
            new PriceReviewDecisionRequestLine(good.Id, "approved", 90000m, null),   // changed, no reason: fine now
            new PriceReviewDecisionRequestLine(noPrice.Id, "approved", null, null),  // cannot be priced
            new PriceReviewDecisionRequestLine(other.Id, "approved", null, null),    // not waiting to be created
            new PriceReviewDecisionRequestLine(good.Id, "approved", null, null),     // duplicate
            new PriceReviewDecisionRequestLine(Guid.NewGuid(), "maybe", null, null), // bad status
        }, "rev", CancellationToken.None);

        Assert.Equal(0, result!.Saved);
        Assert.DoesNotContain(result.Errors, e => e.Error.Contains("reason"));
        Assert.Contains(result.Errors, e => e.LineId == good.Id && e.Error.Contains("more than once"));
        Assert.Contains(result.Errors, e => e.LineId == noPrice.Id && e.Error.Contains("cannot be priced"));
        Assert.Contains(result.Errors, e => e.LineId == other.Id && e.Error.Contains("not waiting to be created"));
        Assert.Contains(result.Errors, e => e.Error.Contains("'approved' or 'held'"));
        repo.Verify(r => r.UpsertAsync(It.IsAny<IReadOnlyList<PartsPriceDecision>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Hold_keeps_the_reviewers_draft_price()
    {
        var line = Line(1, 10m);
        var svc = Service(new[] { line }, out var repo);
        IReadOnlyList<PartsPriceDecision>? saved = null;
        repo.Setup(r => r.UpsertAsync(It.IsAny<IReadOnlyList<PartsPriceDecision>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<PartsPriceDecision> d, CancellationToken _) => saved = d)
            .Returns(Task.CompletedTask);

        var result = await svc.DecideAsync(DocId,
            new[] { new PriceReviewDecisionRequestLine(line.Id, "held", 55000m, "check with supplier") },
            "rev", CancellationToken.None);

        Assert.Empty(result!.Errors);
        Assert.Equal("held", saved!.Single().Status);
        Assert.Equal(55000m, saved!.Single().FinalPl03);
    }

    [Fact]
    public async Task Unknown_document_returns_null()
    {
        var docs = new Mock<IStagingPartsDocumentRepository>();
        var svc = new PriceReviewService(docs.Object, Mock.Of<IPartsReviewRepository>(),
            Mock.Of<IPartsPriceReviewRepository>(), Mock.Of<IForexConversionService>(),
            Mock.Of<IPricingCalculationService>(), Options.Create(new AutohubPricingSettings()));

        Assert.Null(await svc.GetReviewAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
