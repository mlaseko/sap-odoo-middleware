using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Routing with (supplier, article) identity: a donor that is already a SAP item only auto-matches when
/// its supplier AND article equal the line's; a shared-OEM donor of a DIFFERENT article (borrowed /
/// rapidapi) — even under the same supplier — creates a new own-identity item and never reuses the
/// donor's item_code (our generated primary key). Vehicle-group brands → needs_confirmation; a different
/// specific supplier → create-new (cross-supplier); no usable data → needs_manual.
/// </summary>
public class EnrichmentResultRouterTests
{
    // The line's article defaults to the donor's article ("B19124"), so same-supplier tests exercise the
    // same-article auto-match/C2 paths unchanged; the different-article tests pass an explicit mismatch.
    private const string DonorArticle = "B19124";

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

    private static void Donor(Mock<INeonBridgeService> bridge, long id, string? itemCode, string? supplier, string? article = DonorArticle) =>
        bridge.Setup(b => b.GetOitmRowAsync(id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new OitmRow(id, itemCode, article, supplier));

    [Fact]
    public async Task SameSupplier_SameArticle_DonorWithItemCode_AutoMatches()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 1959, "VAG11941", "Borsehung");

        var r = await router.ApplyAsync(lineId, "Borsehung", DonorArticle, Success(1959, "borrowed_oem_bridge"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.AutoMatched, r.Routing);
        Assert.Equal("VAG11941", r.MatchedItemCode);
        Assert.Equal("borrowed_oem_bridge_auto_match", r.MatchStrategy);
        review.Verify(x => x.SetReviewStatusAsync(lineId, "matched", "VAG11941", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SameSupplier_DifferentArticle_DonorWithItemCode_RoutesCreateNew_NeverAutoMatches()
    {
        // The Germax production bug: the donor is a DIFFERENT Germax article reached via a shared OEM that
        // already carries an item_code. Same supplier, different part → own-identity create-new; the
        // donor's internal SKU must NOT become this line's identity.
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 8208, "LR100387", "GERMAX", article: "13-00574-SX");

        var r = await router.ApplyAsync(lineId, "GERMAX", "GL0722", Success(8208, "borrowed_oem_bridge"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.ReadyForReview, r.Routing);
        Assert.Null(r.MatchedItemCode);
        Assert.Equal("borrowed_cross_supplier_create_new", r.MatchStrategy);
        Assert.True(EnrichmentStrategies.IsCrossSupplierStrategy(r.MatchStrategy));   // → own-identity row at Bulk Create
        review.Verify(x => x.SetReviewStatusAsync(It.IsAny<Guid>(), "matched", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SameSupplier_DifferentArticle_DonorNoItemCode_RoutesOwnIdentity_NotWriteToDonor()
    {
        // Same supplier, donor has NO code yet, but a different article → still own-identity create-new,
        // NOT "write our code onto the donor row" (C2) — that donor is a different part.
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 9000, null, "GERMAX", article: "13-00574-SX");

        var r = await router.ApplyAsync(lineId, "GERMAX", "GL0722", Success(9000, "borrowed_oem_bridge"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.ReadyForReview, r.Routing);
        Assert.Equal("borrowed_cross_supplier_create_new", r.MatchStrategy);
        Assert.True(EnrichmentStrategies.IsCrossSupplierStrategy(r.MatchStrategy));
    }

    [Fact]
    public async Task DifferentSupplier_DonorWithItemCode_RoutesToCreateNew_NoMatch()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 805, "BM12850", "DPA");   // donor is DPA, invoice brand is vika

        var r = await router.ApplyAsync(lineId, "vika", DonorArticle, Success(805, "borrowed_oem_bridge"), CancellationToken.None);

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

        var r = await router.ApplyAsync(lineId, "VAG", DonorArticle, Success(1959, "borrowed_oem_bridge"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.NeedsConfirmation, r.Routing);
        Assert.Equal("borrowed_oem_bridge_needs_confirmation", r.MatchStrategy);
        review.Verify(x => x.SetNeedsConfirmationAsync(lineId, "VAG11941", 1959L, "Borsehung",
            "borrowed_oem_bridge_needs_confirmation", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoBrandOnInvoice_DonorWithItemCode_NeedsConfirmation()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 1959, "VAG11941", "Borsehung");

        var r = await router.ApplyAsync(lineId, null, DonorArticle, Success(1959, "borrowed_oem_bridge"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.NeedsConfirmation, r.Routing);
    }

    [Fact]
    public async Task DonorWithoutItemCode_DifferentSupplier_RoutesCrossSupplier()
    {
        // Slice 2.1: a donor with NO item_code under a different supplier must still route cross-supplier
        // (pre-fix this fell through to a plain create-new and minted onto the wrong-supplier donor).
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 2000, null, "Borsehung");

        var r = await router.ApplyAsync(lineId, "vika", DonorArticle, Success(2000, "borrowed_oem_bridge"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.ReadyForReview, r.Routing);
        Assert.Equal("borrowed_cross_supplier_create_new", r.MatchStrategy);
        review.Verify(x => x.SetReviewStatusAsync(It.IsAny<Guid>(), "matched", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GermaxLocal_SameSupplier_SameArticle_AutoMatches()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 3001, "LR15000", "GERMAX");

        var r = await router.ApplyAsync(lineId, "GERMAX", DonorArticle, Success(3001, "germax_local"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.AutoMatched, r.Routing);
        Assert.Equal("germax_local_auto_match", r.MatchStrategy);
    }

    [Fact]
    public async Task GermaxLocal_DifferentSupplier_CrossSupplier()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 3002, "LR15001", "GERMAX");

        var r = await router.ApplyAsync(lineId, "Borsehung", DonorArticle, Success(3002, "germax_local"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.ReadyForReview, r.Routing);
        Assert.Equal("germax_cross_supplier_create_new", r.MatchStrategy);
    }

    [Fact]
    public async Task RapidApi_SameArticle_DonorWithoutItemCode_RoutesToCreateNew()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 4001, null, "BREMBO");   // RapidAPI minted a row with no SAP code yet

        var r = await router.ApplyAsync(lineId, "BREMBO", DonorArticle, Success(4001, "rapidapi_tecdoc_live"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.ReadyForReview, r.Routing);
        Assert.Equal("rapidapi_tecdoc_live_create_new", r.MatchStrategy);
    }

    [Fact]
    public async Task RapidApi_DifferentSupplier_CrossSupplier()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 4002, "BM33000", "BREMBO");

        var r = await router.ApplyAsync(lineId, "MEYLE", DonorArticle, Success(4002, "rapidapi_tecdoc_live"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.ReadyForReview, r.Routing);
        Assert.Equal("rapidapi_cross_supplier_create_new", r.MatchStrategy);
    }

    // ── Slice 2.1: classify supplier identity even when the donor has item_code=NULL (fresh Path E). ──

    [Fact]
    public async Task PathE_DonorNullItemCode_SameSupplier_SameArticle_RoutesC2_WritesToDonor()
    {
        // Donor: supplier=VAICO, item_code=NULL (fresh RapidAPI row); invoice brand=VAICO, same article.
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 10186, null, "VAICO");

        var r = await router.ApplyAsync(lineId, "VAICO", DonorArticle, Success(10186, "rapidapi_tecdoc_live"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.ReadyForReview, r.Routing);
        Assert.Equal("rapidapi_tecdoc_live_create_new", r.MatchStrategy);   // NOT cross-supplier → writes to donor
        Assert.False(EnrichmentStrategies.IsCrossSupplierStrategy(r.MatchStrategy));
        review.Verify(x => x.SetNeedsConfirmationAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PathE_DonorNullItemCode_DifferentSupplier_RoutesCrossSupplier()
    {
        // The production bug: donor supplier=VAICO, item_code=NULL; invoice brand=vika.
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 10186, null, "VAICO");

        var r = await router.ApplyAsync(lineId, "vika", DonorArticle, Success(10186, "rapidapi_tecdoc_live"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.ReadyForReview, r.Routing);
        Assert.Equal("rapidapi_cross_supplier_create_new", r.MatchStrategy);
        Assert.True(EnrichmentStrategies.IsCrossSupplierStrategy(r.MatchStrategy));  // → own-identity row at Bulk Create
    }

    [Fact]
    public async Task PathE_DonorNullItemCode_VehicleGroupBrand_RoutesNeedsConfirmation()
    {
        // Donor: supplier=VAICO, item_code=NULL; invoice brand=VAG (vehicle-group code).
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        Donor(bridge, 10186, null, "VAICO");

        var r = await router.ApplyAsync(lineId, "VAG", DonorArticle, Success(10186, "rapidapi_tecdoc_live"), CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.NeedsConfirmation, r.Routing);
        Assert.Equal("rapidapi_needs_confirmation", r.MatchStrategy);
        review.Verify(x => x.SetNeedsConfirmationAsync(lineId, null, 10186L, "VAICO",
            "rapidapi_needs_confirmation", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PartialEnrichment_NeedsManual_NoDonorLookup()
    {
        var (router, review, bridge) = Build();
        var lineId = Guid.NewGuid();
        var enr = new EnrichmentResponse { Status = "partial", Source = "unmatched", EnrichmentSource = "unmatched", ItemData = null };

        var r = await router.ApplyAsync(lineId, "vika", "GL0722", enr, CancellationToken.None);

        Assert.Equal(LineEnrichmentRouting.NeedsManual, r.Routing);
        review.Verify(x => x.SetReviewStatusAsync(lineId, "needs_manual", null, It.IsAny<CancellationToken>()), Times.Once);
        bridge.Verify(x => x.GetOitmRowAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
