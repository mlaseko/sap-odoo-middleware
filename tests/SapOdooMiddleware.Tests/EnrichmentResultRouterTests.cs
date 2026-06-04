using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Path C1 routing: a borrowed/direct enrichment whose donor oitm row already carries a SAP item_code
/// must AUTO-MATCH (no creation, no confirmation), while a donor without one stays a create-new (C2),
/// and a failed/partial result goes to needs_manual.
/// </summary>
public class EnrichmentResultRouterTests
{
    private static EnrichmentResponse Success(int? oitm, string source, bool confirmationRequired) => new()
    {
        Status = "success",
        Source = source,
        EnrichmentSource = source,
        NeonOitmId = oitm,
        ConfirmationRequired = confirmationRequired,
        ItemData = new EnrichmentItemData
        {
            PrimaryDescription = "Timing chain tensioner",
            SuggestedItmsGrpCod = 105,
            SuggestedSkuPrefix = "VAG",
        },
        BorrowedFrom = source == "borrowed_oem_bridge"
            ? new BorrowedFrom { ArticleNumber = "B19124", SupplierName = "Borsehung", MatchViaOem = "06J109259A" }
            : null,
    };

    private static (EnrichmentResultRouter router, Mock<IPartsReviewRepository> review, Mock<INeonBridgeService> bridge) Build()
    {
        var review = new Mock<IPartsReviewRepository>();
        var bridge = new Mock<INeonBridgeService>();
        var router = new EnrichmentResultRouter(review.Object, bridge.Object, new Mock<ILogger<EnrichmentResultRouter>>().Object);
        return (router, review, bridge);
    }

    [Fact]
    public async Task BorrowedDonorWithItemCode_AutoMatches_NoCreate()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        bridge.Setup(b => b.GetItemCodeAsync(1959, It.IsAny<CancellationToken>())).ReturnsAsync("VAG11941");

        var r = await router.ApplyAsync(lineId, Success(1959, "borrowed_oem_bridge", confirmationRequired: true), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.AutoMatched, r.Routing);
        Assert.Equal("VAG11941", r.MatchedItemCode);
        Assert.Equal("borrowed_oem_bridge_auto_match", r.MatchStrategy);
        review.Verify(x => x.SetReviewStatusAsync(lineId, "matched", "VAG11941", It.IsAny<CancellationToken>()), Times.Once);
        // Persisted with confirmation no longer required (it's a match) and the auto-match strategy.
        review.Verify(x => x.RecordEnrichmentResultAsync(lineId, "borrowed_oem_bridge",
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long?>(), false, "success",
            It.IsAny<string?>(), "borrowed_oem_bridge_auto_match", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BorrowedDonorWithoutItemCode_ReadyForReview()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        bridge.Setup(b => b.GetItemCodeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var r = await router.ApplyAsync(lineId, Success(2000, "borrowed_oem_bridge", confirmationRequired: true), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.ReadyForReview, r.Routing);
        Assert.Null(r.MatchedItemCode);
        Assert.Equal("borrowed_oem_bridge_create_new", r.MatchStrategy);
        review.Verify(x => x.SetReviewStatusAsync(It.IsAny<Guid>(), "matched", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TecdocDirectWithItemCode_AutoMatches_DirectStrategy()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        bridge.Setup(b => b.GetItemCodeAsync(805, It.IsAny<CancellationToken>())).ReturnsAsync("VAG10690");

        var r = await router.ApplyAsync(lineId, Success(805, "tecdoc_direct", confirmationRequired: false), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.AutoMatched, r.Routing);
        Assert.Equal("VAG10690", r.MatchedItemCode);
        Assert.Equal("enrichment_direct_auto_match", r.MatchStrategy);
        review.Verify(x => x.SetReviewStatusAsync(lineId, "matched", "VAG10690", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PartialEnrichment_NeedsManual_NoBridgeLookup()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        var enr = new EnrichmentResponse { Status = "partial", Source = "unmatched", EnrichmentSource = "unmatched", ItemData = null };

        var r = await router.ApplyAsync(lineId, enr, CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.NeedsManual, r.Routing);
        review.Verify(x => x.SetReviewStatusAsync(lineId, "needs_manual", null, It.IsAny<CancellationToken>()), Times.Once);
        bridge.Verify(x => x.GetItemCodeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
