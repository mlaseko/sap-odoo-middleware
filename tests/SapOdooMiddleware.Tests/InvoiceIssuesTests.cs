using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>Phase 1: each line's next action, the grouped issues list, and the bulk actions.</summary>
public class InvoiceIssuesTests
{
    private static readonly Guid DocId = Guid.NewGuid();

    private static PartsLineState L(
        string status = "pending", int n = 1, Guid? id = null, decimal? price = 10m, decimal? qty = 1m,
        string? source = "rapidapi_tecdoc_live", bool itemData = true, bool confirmationRequired = false,
        bool confirmed = false, bool needsReview = false, string? verdict = null,
        string? marqueResolved = null, string? marquePrefix = null, string? skuPrefix = "MB",
        string? suggested = null, bool promo = false, string? error = null) =>
        new(id ?? Guid.NewGuid(), n, $"ART{n}", $"Part {n}", "VIKA", qty, price, promo, status, error,
            source, itemData, confirmationRequired, confirmed, needsReview, verdict, "Generator", "BOSCH",
            marqueResolved, marquePrefix, "BM", skuPrefix, 106, suggested, "VIKA", null, null, "1234", "TOPRAN");

    private static string Next(PartsLineState l, LinePriceState? p = null) => LineIssueClassifier.Classify(l, p).NextAction;

    // ── Classifier ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("created", "created")]
    [InlineData("matched", "matched")]
    [InlineData("skip", "skipped")]
    public void Done_lines_have_no_issues(string status, string expected)
    {
        var r = LineIssueClassifier.Classify(L(status, price: null, needsReview: true), null);
        Assert.Equal(expected, r.NextAction);
        Assert.True(r.IsDone);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Each_situation_gets_its_next_action()
    {
        Assert.Equal("create_failed", Next(L("create_failed", error: "SAP said no")));
        Assert.Equal("fix_data", Next(L("pending", price: null)));
        Assert.Equal("fix_data", Next(L("create_new", qty: 0m)));
        Assert.Equal("confirm_match", Next(L("needs_confirmation", suggested: "VK100")));
        Assert.Equal("check_part", Next(L("pending", needsReview: true, verdict: "UNCERTAIN")));
        Assert.Equal("manual", Next(L("needs_manual")));
        Assert.Equal("awaiting_enrichment", Next(L("pending", source: null, itemData: false, skuPrefix: null)));
        Assert.Equal("assign_marque", Next(L("pending", marqueResolved: "false", skuPrefix: "GEN")));
        Assert.Equal("assign_marque", Next(L("needs_manufacturer")));
        Assert.Equal("assign_marque", Next(L("create_new", skuPrefix: null)));          // item data, no prefix, no ruling
        Assert.Equal("accept_marque", Next(L("pending", marqueResolved: "true", marquePrefix: "MB", skuPrefix: "GEN")));
        Assert.Equal("sku_range_full", Next(L("prefix_exhausted")));
        Assert.Equal("confirm_borrow", Next(L("create_new", confirmationRequired: true)));
        Assert.Equal("queue", Next(L("pending")));
        Assert.Equal("ready", Next(L("create_new")));
        Assert.Equal("ready", Next(L("create_new"), new LinePriceState("approved")));
    }

    [Theory]
    [InlineData("none", "Price not reviewed yet.")]
    [InlineData("held", "Held in the price review.")]
    [InlineData("stale", "out of date")]
    public void Queued_lines_without_an_approved_price_need_the_price_review(string state, string text)
    {
        var r = LineIssueClassifier.Classify(L("create_new"), new LinePriceState(state));
        Assert.Equal("price", r.NextAction);
        Assert.Contains(text, r.Issues.Single().Text);
    }

    [Fact]
    public void The_part_is_checked_before_the_marque_and_every_issue_is_listed()
    {
        var r = LineIssueClassifier.Classify(
            L("pending", needsReview: true, marqueResolved: "false", skuPrefix: "GEN", price: null), null);

        Assert.Equal("fix_data", r.NextAction);
        Assert.Equal(new[] { "fix_data", "check_part", "assign_marque" }, r.Issues.Select(i => i.Code));
    }

    [Fact]
    public void Confirmed_part_and_borrow_are_no_longer_issues_and_promotions_need_no_price()
    {
        Assert.Equal("ready", Next(L("create_new", needsReview: true, confirmationRequired: true, confirmed: true)));
        Assert.Equal("queue", Next(L("pending", price: null, promo: true)));
    }

    // ── Service ───────────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public Mock<IPartsReviewRepository> Review = new();
        public Mock<IPartsLineStateRepository> Lines = new();
        public Mock<IMarqueAssignmentService> Marque = new();
        public Mock<IAutohubSapB1Service> Sap = new();
        public Mock<IPriceReviewService> Price = new();
        public string DocStatus = "extracted";
        public List<PartsLineState> State = new();
        public List<PriceReviewLine> PriceLines = new();

        public InvoiceIssuesService Build(bool requireReview = false)
        {
            var docs = new Mock<IStagingPartsDocumentRepository>();
            docs.Setup(d => d.GetByIdAsync(DocId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StagingPartsDocumentRow(DocId, "f.pdf", "/f", "sha", 1, DocStatus, "ok", null,
                    DateTime.UtcNow, DateTime.UtcNow, 1, null, null, "VIKA", "INV", DateTime.UtcNow, "USD", 1m));
            Lines.Setup(r => r.ListAsync(DocId, It.IsAny<CancellationToken>())).ReturnsAsync(() => State);
            Price.Setup(p => p.GetReviewAsync(DocId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new PriceReviewDocument(DocId, "USD", 2700m, null,
                    new PriceReviewSummary(0, 0, 0, 0, 0, 0, 0), PriceLines));
            var jobs = new AutohubBulkCreateJobService(Mock.Of<IServiceScopeFactory>(), Mock.Of<IHostApplicationLifetime>(),
                NullLogger<AutohubBulkCreateJobService>.Instance);
            return new InvoiceIssuesService(docs.Object, Lines.Object, Review.Object, Price.Object, Marque.Object,
                Sap.Object, jobs, Options.Create(new AutohubPricingSettings { RequirePriceReview = requireReview }));
        }
    }

    private static PriceReviewLine PriceLine(Guid id, PriceReviewDecisionView? d) =>
        new(id, 1, "A", "P", "VIKA", 1m, 10m, null, 10m, 27000m, 33750m, 100000m, 70000m, 0.6m, 0.3m, "VIKA",
            false, null, 500, Array.Empty<PriceReviewFlag>(), null, d);

    private static PriceReviewDecisionView Decision(string status, decimal pl03) =>
        new(status, pl03, null, null, "market", "rev", DateTime.UtcNow, false, Array.Empty<string>());

    [Fact]
    public async Task Issues_are_grouped_by_next_action_in_order_of_importance()
    {
        var rig = new Rig();
        var queued = L("create_new", n: 5);
        rig.State = new()
        {
            L("created", n: 1), L("matched", n: 2), L("skip", n: 3),
            L("pending", n: 4),                                                       // queue
            queued,                                                                  // price (none)
            L("pending", n: 6, marqueResolved: "true", marquePrefix: "MB", skuPrefix: "GEN"),   // accept_marque
            L("pending", n: 7, needsReview: true),                                   // check_part
            L("create_new", n: 8, id: Guid.NewGuid()),                               // ready (approved below)
        };
        rig.PriceLines = new() { PriceLine(queued.Id, null), PriceLine(rig.State[7].Id, Decision("approved", 100000m)) };

        var r = await rig.Build().GetAsync(DocId, CancellationToken.None);

        Assert.Equal(new[] { "check_part", "accept_marque", "queue", "price" }, r!.Groups.Select(g => g.Code));
        Assert.Equal(8, r.Summary.Total);
        Assert.Equal(3, r.Summary.Done);
        Assert.Equal(1, r.Summary.Ready);
        Assert.Equal(4, r.Summary.WithIssues);
        var accept = r.Groups.Single(g => g.Code == "accept_marque");
        Assert.Equal("MB", accept.Lines.Single().MarqueSuggestion);
        Assert.Contains("accept_marque", accept.BulkActions);
        Assert.Equal(new[] { "open_prices" }, r.Groups.Single(g => g.Code == "price").BulkActions);
    }

    [Fact]
    public async Task Skip_queue_and_confirm_check_each_line_on_its_own()
    {
        var rig = new Rig();
        var created = L("created", n: 1);
        var pending = L("pending", n: 2);
        var notLookedUp = L("pending", n: 3, source: null, itemData: false);
        var check = L("pending", n: 4, needsReview: true);
        rig.State = new() { created, pending, notLookedUp, check };
        var svc = rig.Build();

        var skip = await svc.ApplyAsync(DocId, new IssueBulkRequest("skip", new[] { created.Id, pending.Id, Guid.NewGuid() }, null, null), "me", default);
        Assert.Equal(1, skip!.Succeeded);
        Assert.Equal(2, skip.Failed);
        Assert.Contains("already created", skip.Results[0].Error);
        Assert.Contains("not found", skip.Results[2].Error);
        rig.Review.Verify(r => r.SetReviewStatusAsync(pending.Id, "skip", null, It.IsAny<CancellationToken>()), Times.Once);

        var queue = await svc.ApplyAsync(DocId, new IssueBulkRequest("queue", new[] { pending.Id, notLookedUp.Id }, null, null), "me", default);
        Assert.Equal(new[] { true, false }, queue!.Results.Select(x => x.Ok));
        Assert.Contains("not been looked up", queue.Results[1].Error);

        var confirm = await svc.ApplyAsync(DocId, new IssueBulkRequest("confirm_and_queue", new[] { check.Id }, null, null), "Mohamed", default);
        Assert.True(confirm!.Results.Single().Ok);
        rig.Review.Verify(r => r.ConfirmEnrichmentAsync(check.Id, "Mohamed", It.IsAny<CancellationToken>()), Times.Once);
        rig.Review.Verify(r => r.SetReviewStatusAsync(check.Id, "create_new", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Suggested_match_is_used_only_when_it_exists_in_SAP()
    {
        var rig = new Rig();
        var ok = L("needs_confirmation", n: 1, suggested: "VK100");
        var missing = L("needs_confirmation", n: 2, suggested: "VK999");
        var none = L("needs_confirmation", n: 3);
        rig.State = new() { ok, missing, none };
        rig.Sap.Setup(s => s.ItemExistsAsync("VK100")).ReturnsAsync(true);
        rig.Sap.Setup(s => s.ItemExistsAsync("VK999")).ReturnsAsync(false);

        var r = await rig.Build().ApplyAsync(DocId,
            new IssueBulkRequest("use_suggested_match", new[] { ok.Id, missing.Id, none.Id }, null, null), "me", default);

        Assert.Equal(new[] { true, false, false }, r!.Results.Select(x => x.Ok));
        Assert.Contains("does not exist", r.Results[1].Error);
        rig.Review.Verify(x => x.SetReviewStatusAsync(ok.Id, "matched", "VK100", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Marques_accepted_or_assigned_per_line()
    {
        var rig = new Rig();
        var a = L("pending", n: 1, marqueResolved: "true", marquePrefix: "MB", skuPrefix: "GEN");
        var b = L("pending", n: 2, marqueResolved: "false", skuPrefix: "GEN");
        rig.State = new() { a, b };
        rig.Lines.Setup(r => r.ApplyResolvedMarquesAsync(DocId, It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { a.Id });
        rig.Marque.Setup(m => m.AssignAsync(DocId, b.Id, "BM", 105, "me", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarqueAssignResult(MarqueAssignKind.Ok, null, null));
        rig.Marque.Setup(m => m.AssignAsync(DocId, a.Id, "BM", 105, "me", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarqueAssignResult(MarqueAssignKind.Upstream, "DGX could not resolve 'BM'", null));
        var svc = rig.Build();

        var accept = await svc.ApplyAsync(DocId, new IssueBulkRequest("accept_marque", new[] { a.Id, b.Id }, null, null), "me", default);
        Assert.Equal(new[] { true, false }, accept!.Results.Select(x => x.Ok));

        var assign = await svc.ApplyAsync(DocId, new IssueBulkRequest("assign_marque", new[] { b.Id, a.Id }, "BM", 105), "me", default);
        Assert.Equal(new[] { true, false }, assign!.Results.Select(x => x.Ok));
        Assert.Contains("could not resolve", assign.Results[1].Error);
    }

    [Fact]
    public async Task Retry_honours_the_price_review_and_starts_a_job()
    {
        var rig = new Rig();
        var held = L("create_failed", n: 1);
        var approved = L("create_failed", n: 2);
        var notFailed = L("create_new", n: 3);
        rig.State = new() { held, approved, notFailed };
        rig.PriceLines = new() { PriceLine(held.Id, Decision("held", 100000m)), PriceLine(approved.Id, Decision("approved", 120000m)) };

        var r = await rig.Build().ApplyAsync(DocId,
            new IssueBulkRequest("retry_create", new[] { held.Id, approved.Id, notFailed.Id }, null, null), "me", default);

        Assert.Equal(new[] { false, true, false }, r!.Results.Select(x => x.Ok));
        Assert.Contains("Held", r.Results[0].Error);
        Assert.NotNull(r.JobId);
    }

    [Fact]
    public async Task Bad_requests_are_rejected()
    {
        var rig = new Rig();
        rig.State = new() { L() };
        var svc = rig.Build();

        await Assert.ThrowsAsync<ArgumentException>(() => svc.ApplyAsync(DocId, new IssueBulkRequest("explode", new[] { rig.State[0].Id }, null, null), "me", default));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.ApplyAsync(DocId, new IssueBulkRequest("skip", Array.Empty<Guid>(), null, null), "me", default));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.ApplyAsync(DocId, new IssueBulkRequest("assign_marque", new[] { rig.State[0].Id }, " ", null), "me", default));

        rig.DocStatus = "reviewed";
        var reviewed = rig.Build();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => reviewed.ApplyAsync(DocId, new IssueBulkRequest("skip", new[] { rig.State[0].Id }, null, null), "me", default));
        Assert.Contains("reviewed", ex.Message);
    }

    // ── Marque assignment guards (moved from the controller) ──────────────────

    [Fact]
    public async Task Marque_assignment_rejects_GEN_and_terminal_lines()
    {
        var review = new Mock<IPartsReviewRepository>();
        var lineId = Guid.NewGuid();
        review.Setup(r => r.GetByIdAsync(lineId, It.IsAny<CancellationToken>())).ReturnsAsync(new PartsReviewLineRow(
            lineId, DocId, 1, 1, "A", new List<string>(), "d", "VIKA", 1m, "PC", 10m, null, null, false, "created",
            null, null, null, null, null, null, null, null, null, null, null, false, null, null, null, false, false));
        var svc = new MarqueAssignmentService(review.Object, Mock.Of<IManufacturerResolutionClient>());

        Assert.Equal(MarqueAssignKind.BadRequest, (await svc.AssignAsync(DocId, lineId, "GEN", null, "me", default)).Kind);
        Assert.Equal(MarqueAssignKind.NotFound, (await svc.AssignAsync(Guid.NewGuid(), lineId, "MB", null, "me", default)).Kind);
        Assert.Equal(MarqueAssignKind.Conflict, (await svc.AssignAsync(DocId, lineId, "MB", null, "me", default)).Kind);
    }
}
