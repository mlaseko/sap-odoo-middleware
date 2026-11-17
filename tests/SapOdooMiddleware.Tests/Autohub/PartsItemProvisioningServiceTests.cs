using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;
using SapOdooMiddleware.Services.Autohub;
using Xunit;

namespace SapOdooMiddleware.Tests.Autohub;

/// <summary>Bulk Create's duplicate check (SAP first, Neon only as a bounded fallback) and the post-commit steps.</summary>
public class PartsItemProvisioningServiceTests
{
    private readonly Mock<IEnrichmentService> _enrichment = new();
    private readonly Mock<IForexConversionService> _forex = new();
    private readonly Mock<IPricingCalculationService> _pricing = new();
    private readonly Mock<ISkuGenerationService> _sku = new();
    private readonly Mock<IOemFilterService> _filter = new();
    private readonly Mock<IAutohubSapB1Service> _sap = new();
    private readonly Mock<INeonBridgeService> _bridge = new();
    private readonly Mock<IPartsReviewRepository> _review = new();
    private readonly Mock<IPricingOverrideRepository> _overrides = new();
    private readonly Mock<IAutohubInventorySqlService> _sapSql = new();

    private PartsItemProvisioningService Service() => new(
        _enrichment.Object, _forex.Object, _pricing.Object, _sku.Object, _filter.Object, _sap.Object,
        _bridge.Object, _review.Object, _overrides.Object, _sapSql.Object,
        NullLogger<PartsItemProvisioningService>.Instance);

    private static PartsProvisioningLine Line() => new(
        Guid.NewGuid(), "B12746", new List<string> { "1K0407151" }, "VIKA", "Control arm",
        UnitPriceForeign: 10m, EnrichmentConfirmed: true, NeonOitmId: null, EnrichmentPayloadJson: null,
        MatchStrategy: null);

    [Fact]
    public async Task AnItemAlreadyInSapIsMatchedWithoutAskingNeonOrCreating()
    {
        var line = Line();
        _sapSql.Setup(s => s.FindItemCodeByArticleAsync("B12746", "VIKA", It.IsAny<CancellationToken>()))
            .ReturnsAsync("VAG12331");

        var outcome = await Service().ProvisionAsync(line, "EUR", CancellationToken.None);

        Assert.Equal("created", outcome.Status);
        Assert.Equal("VAG12331", outcome.ItemCode);
        _review.Verify(r => r.SetReviewStatusAsync(line.Id, "matched", "VAG12331", It.IsAny<CancellationToken>()), Times.Once);
        _bridge.Verify(b => b.FindItemCodeByArticleSupplierAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _sap.Verify(s => s.CreateAutohubItemAsync(It.IsAny<SapAutohubItemRequest>()), Times.Never);
    }

    [Fact]
    public async Task WhenSapCannotBeAskedTheCatalogueCodeIsUsedOnlyIfSapHasIt()
    {
        var line = Line();
        _sapSql.Setup(s => s.FindItemCodeByArticleAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SQL down"));
        _bridge.Setup(b => b.FindItemCodeByArticleSupplierAsync("B12746", "VIKA", It.IsAny<CancellationToken>()))
            .ReturnsAsync("VAG12331");
        _sap.Setup(s => s.ItemExistsAsync("VAG12331")).ReturnsAsync(true);

        var outcome = await Service().ProvisionAsync(line, "EUR", CancellationToken.None);

        Assert.Equal("VAG12331", outcome.ItemCode);
        _review.Verify(r => r.SetReviewStatusAsync(line.Id, "matched", "VAG12331", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AFailedCatalogueLookupDoesNotFailTheLine()
    {
        var line = Line();
        _sapSql.Setup(s => s.FindItemCodeByArticleAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SQL down"));
        _bridge.Setup(b => b.FindItemCodeByArticleSupplierAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Npgsql.NpgsqlException("Exception while reading from stream"));
        SetUpCreatePath();

        var outcome = await Service().ProvisionAsync(line, "EUR", CancellationToken.None);

        Assert.Equal("created", outcome.Status);
        Assert.Equal("VAG20500", outcome.ItemCode);
        _sap.Verify(s => s.CreateAutohubItemAsync(It.IsAny<SapAutohubItemRequest>()), Times.Once);
    }

    [Fact]
    public async Task AfterTheSapCreateTheLineIsRecordedCreatedEvenWhenTheTimeLimitRunsOut()
    {
        var line = Line();
        using var cts = new CancellationTokenSource();
        SetUpCreatePath();
        // The per-item limit runs out while SAP is creating the item.
        _sap.Setup(s => s.CreateAutohubItemAsync(It.IsAny<SapAutohubItemRequest>()))
            .Callback(() => cts.Cancel()).Returns(Task.CompletedTask);
        CancellationToken recordToken = default;
        _review.Setup(r => r.RecordCreatedAsync(line.Id, "VAG20500", 100m, 250m, 200m, It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, decimal, decimal, decimal, decimal, CancellationToken>((_, _, _, _, _, _, t) => recordToken = t)
            .Returns(Task.CompletedTask);

        var outcome = await Service().ProvisionAsync(line, "EUR", cts.Token);

        Assert.Equal("created", outcome.Status);
        Assert.Equal("VAG20500", outcome.ItemCode);
        Assert.False(recordToken.CanBeCanceled);
        _review.Verify(r => r.RecordCreateFailedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void SetUpCreatePath()
    {
        _filter.Setup(f => f.Filter(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(new OemFilterResult { CleanOems = new[] { "1K0407151" }, NoiseFiltered = Array.Empty<string>(), Source = "regex_blacklist" });
        _enrichment.Setup(e => e.EnrichLineAsync(It.IsAny<EnrichmentInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnrichmentResponse
            {
                ItemData = new EnrichmentItemData { SuggestedItmsGrpCod = 101, SuggestedSkuPrefix = "VAG", PrimaryDescription = "Control arm" },
            });
        _forex.Setup(f => f.ConvertToTzsAsync(It.IsAny<decimal>(), "EUR", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(30_000m);
        _pricing.Setup(p => p.CalculateDetailedAsync(It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PricingQuote(30_000m, 100m, 250m, 200m, 2.5m, "VIKA", 0m, null, false, null, 1));
        _sku.Setup(s => s.GenerateAsync("VAG", It.IsAny<CancellationToken>())).ReturnsAsync("VAG20500");
        _bridge.Setup(b => b.GetGermaxAlternateArticleNumbersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
    }
}
