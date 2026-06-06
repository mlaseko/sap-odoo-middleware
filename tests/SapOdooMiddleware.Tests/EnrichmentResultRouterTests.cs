using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Routing with supplier identity (Slice 1.6): a donor that is already a SAP item only auto-matches
/// when its supplier equals the invoice brand; vehicle-group brands → needs_confirmation; a different
/// specific supplier → create-new (borrowed cross-supplier); no usable data → needs_manual.
/// </summary>
public class EnrichmentResultRouterTests
{
    private static EnrichmentResponse Success(int? oitm, string source) => new()
    {
        Status = "success",
        Source = source,
        EnrichmentSource = source,
        NeonOitmId = oitm,
        ConfirmationRequired = source == "borrowed_oem_bridge",
        ItemData = new EnrichmentItemData { PrimaryDescription = "Timing chain tensioner", SuggestedItmsGrpCod = 105, SuggestedSkuPrefix = "VAG" },
        BorrowedFrom = source == "borrowed_oem_bridge"
            ? new BorrowedFrom { ArticleNumber = "B19124", SupplierName = "Borsehung" }
            : null,
    };

    private static (EnrichmentResultRouter router, Mock<IPartsReviewRepository> review, Mock<INeonBridgeService> bridge) Build()
    {
        var review = new Mock<IPartsReviewRepository>();
        var bridge = new Mock<INeonBridgeService>();
        var router = new EnrichmentResultRouter(review.Object, bridge.Object, new Mock<ILogger<EnrichmentResultRouter>>().Object);
        return (router, review, bridge);
    }

    private static void Donor(Mock<INeonBridgeService> bridge, long id, string? itemCode, string? supplier) =>
        bridge.Setup(b => b.GetOitmRowAsync(id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new OitmRow(id, itemCode, "B19124", supplier));

    [Fact]
    public async Task SameSupplier_DonorWithItemCode_AutoMatches()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 1959, "VAG11941", "Borsehung");

        var r = await router.ApplyAsync(lineId, "Borsehung", Success(1959, "borrowed_oem_bridge"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.AutoMatched, r.Routing);
        Assert.Equal("VAG11941", r.MatchedItemCode);
        Assert.Equal("borrowed_oem_bridge_auto_match", r.MatchStrategy);
        review.Verify(x => x.SetReviewStatusAsync(lineId, "matched", "VAG11941", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DifferentSupplier_DonorWithItemCode_RoutesToCreateNew_NoMatch()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 805, "BM12850", "DPA");   // donor is DPA, invoice brand is vika

        var r = await router.ApplyAsync(lineId, "vika", Success(805, "borrowed_oem_bridge"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.ReadyForReview, r.Routing);
        Assert.Null(r.MatchedItemCode);
        Assert.Equal("borrowed_cross_supplier_create_new", r.MatchStrategy);
        review.Verify(x => x.SetReviewStatusAsync(It.IsAny<Guid>(), "matched", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VehicleGroupBrand_DonorWithItemCode_NeedsConfirmation()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 1959, "VAG11941", "Borsehung");

        var r = await router.ApplyAsync(lineId, "VAG", Success(1959, "borrowed_oem_bridge"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.NeedsConfirmation, r.Routing);
        Assert.Equal("vehicle_group_brand_needs_confirmation", r.MatchStrategy);
        review.Verify(x => x.SetNeedsConfirmationAsync(lineId, "VAG11941", 1959L, "Borsehung",
            "vehicle_group_brand_needs_confirmation", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoBrandOnInvoice_DonorWithItemCode_NeedsConfirmation()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 1959, "VAG11941", "Borsehung");

        var r = await router.ApplyAsync(lineId, null, Success(1959, "borrowed_oem_bridge"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.NeedsConfirmation, r.Routing);
    }

    [Fact]
    public async Task DonorWithoutItemCode_ReadyForReview()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 2000, null, "Borsehung");   // donor not yet a SAP item

        var r = await router.ApplyAsync(lineId, "vika", Success(2000, "borrowed_oem_bridge"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.ReadyForReview, r.Routing);
        Assert.Equal("borrowed_oem_bridge_create_new", r.MatchStrategy);
        review.Verify(x => x.SetReviewStatusAsync(It.IsAny<Guid>(), "matched", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PartialEnrichment_NeedsManual_NoDonorLookup()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        var enr = new EnrichmentResponse { Status = "partial", Source = "unmatched", EnrichmentSource = "unmatched", ItemData = null };

        var r = await router.ApplyAsync(lineId, "vika", enr, CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.NeedsManual, r.Routing);
        review.Verify(x => x.SetReviewStatusAsync(lineId, "needs_manual", null, It.IsAny<CancellationToken>()), Times.Once);
        bridge.Verify(x => x.GetOitmRowAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
