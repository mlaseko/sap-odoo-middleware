using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Price review step 5: Bulk Create follows the price review. Approved lines are created at their
/// approved PL03, held lines are skipped, and unreviewed lines are created at the calculated price
/// unless AutohubPricing:RequirePriceReview is on.
/// </summary>
public class PartsItemCreationPriceReviewTests
{
    private static readonly Guid DocId = Guid.NewGuid();

    private static PartsProvisioningLine Prov(Guid id) =>
        new(id, "ART", new List<string>(), "VIKA", "Part", 10m, true, null, null, null);

    private static PriceReviewDecisionView Decision(string status, decimal? finalPl03, bool stale = false) =>
        new(status, finalPl03, null, null, finalPl03 == 100000m ? null : "market price", "Mohamed", DateTime.UtcNow,
            stale, stale ? new[] { "Exchange rate changed." } : Array.Empty<string>());

    private static PriceReviewLine Review(Guid id, PriceReviewDecisionView? decision, decimal formulaPl03 = 100000m) =>
        new(id, 1, "ART", "Part", "VIKA", 1m, 10m, null, 10m, 27000m, 33750m, formulaPl03, 70000m, 0.66m,
            0.3m, "VIKA", false, null, 500, Array.Empty<PriceReviewFlag>(), null, decision);

    private static (PartsItemCreationService Svc, List<(Guid Id, PartsLineOverride? Override)> Provisioned) Build(
        IReadOnlyList<(Guid Id, PriceReviewDecisionView? Decision)> lines, bool required)
    {
        var provisioned = new List<(Guid, PartsLineOverride?)>();
        var prov = new Mock<IPartsItemProvisioningService>();
        prov.Setup(p => p.ProvisionAsync(It.IsAny<PartsProvisioningLine>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<PartsLineOverride?>()))
            .Callback((PartsProvisioningLine l, string? _, CancellationToken _, PartsLineOverride? o) => provisioned.Add((l.Id, o)))
            .ReturnsAsync(new PartsProvisioningOutcome("created", "VK1", null));

        var repo = new Mock<IPartsReviewRepository>();
        repo.Setup(r => r.ListCreateNewAsync(DocId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lines.Select(l => Prov(l.Id)).ToList());

        var docs = new Mock<IStagingPartsDocumentRepository>();
        docs.Setup(d => d.GetByIdAsync(DocId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StagingPartsDocumentRow(DocId, "f.pdf", "/f.pdf", "sha", 1, "extracted", "ok", null,
                DateTime.UtcNow, DateTime.UtcNow, 1, null, null, "VIKA", "INV", DateTime.UtcNow, "USD", 1m));

        var review = new Mock<IPriceReviewService>();
        review.Setup(r => r.GetReviewAsync(DocId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PriceReviewDocument(DocId, "USD", 2700m, null, new PriceReviewSummary(0, 0, 0, 0, 0, 0, 0),
                lines.Select(l => Review(l.Id, l.Decision)).ToList()));

        var svc = new PartsItemCreationService(prov.Object, repo.Object, docs.Object, review.Object,
            Options.Create(new AutohubPricingSettings { RequirePriceReview = required }),
            NullLogger<PartsItemCreationService>.Instance);
        return (svc, provisioned);
    }

    [Fact]
    public async Task Review_optional_approved_at_their_price_held_skipped_unreviewed_at_calculated_price()
    {
        Guid changed = Guid.NewGuid(), same = Guid.NewGuid(), held = Guid.NewGuid(), none = Guid.NewGuid(), stale = Guid.NewGuid();
        var (svc, provisioned) = Build(new (Guid, PriceReviewDecisionView?)[]
        {
            (changed, Decision("approved", 120000m)),
            (same, Decision("approved", 100000m)),
            (held, Decision("held", 90000m)),
            (none, null),
            (stale, Decision("approved", 130000m, stale: true)),
        }, required: false);

        var result = await svc.BulkCreateAsync(DocId, CancellationToken.None);

        Assert.Equal(new[] { changed, same, none, stale }, provisioned.Select(p => p.Id));
        var o = provisioned.Single(p => p.Id == changed).Override!;
        Assert.Equal(120000m, o.Pl03Override);
        Assert.Equal("market price", o.OverrideReason);
        Assert.Equal("Mohamed", o.RequestedBy);
        Assert.Null(o.UnitPriceOverride);
        Assert.Null(o.CifOverride);
        Assert.Null(provisioned.Single(p => p.Id == same).Override);    // unchanged price: no override record
        Assert.Null(provisioned.Single(p => p.Id == stale).Override);   // stale approval: calculated price
        Assert.Equal(4, result.Attempted);
        Assert.Equal(4, result.Created);
        Assert.Equal(1, result.PriceHeld);
        Assert.Equal(0, result.NotPriceApproved);
    }

    [Fact]
    public async Task Review_required_creates_only_current_approvals()
    {
        Guid approved = Guid.NewGuid(), held = Guid.NewGuid(), none = Guid.NewGuid(), stale = Guid.NewGuid();
        var (svc, provisioned) = Build(new (Guid, PriceReviewDecisionView?)[]
        {
            (approved, Decision("approved", 120000m)),
            (held, Decision("held", null)),
            (none, null),
            (stale, Decision("approved", 130000m, stale: true)),
        }, required: true);

        var result = await svc.BulkCreateAsync(DocId, CancellationToken.None);

        Assert.Equal(approved, Assert.Single(provisioned).Id);
        Assert.Equal(1, result.Attempted);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.PriceHeld);
        Assert.Equal(2, result.NotPriceApproved);
    }

    [Fact]
    public async Task Review_required_rejects_an_explicit_selection_that_is_not_approved()
    {
        Guid approved = Guid.NewGuid(), none = Guid.NewGuid();
        var (svc, provisioned) = Build(new (Guid, PriceReviewDecisionView?)[]
        {
            (approved, Decision("approved", 100000m)), (none, null),
        }, required: true);

        var result = await svc.BulkCreateAsync(DocId, CancellationToken.None, new[]
        {
            new BulkCreateLineSelection(approved, null), new BulkCreateLineSelection(none, null),
        });

        Assert.Equal(approved, Assert.Single(provisioned).Id);
        var f = Assert.Single(result.Failures);
        Assert.Equal(none, f.LineId);
        Assert.Contains("not approved in the price review", f.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_line_missing_from_the_review_follows_the_setting(bool required)
    {
        var (action, o) = PartsItemCreationService.ApplyPriceReview(null, required);
        Assert.Equal(required ? PartsItemCreationService.PriceReviewAction.NotApproved
                              : PartsItemCreationService.PriceReviewAction.Create, action);
        Assert.Null(o);
    }
}
