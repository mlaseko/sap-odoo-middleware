using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MolasLubes.Infrastructure.Integrations.LiquiMoly;
using Moq;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Integrations.Classifier;
using SapOdooMiddleware.ItemProvisioning;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Pricing;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests.ItemProvisioning;

/// <summary>PreviewAsync with the article already in the Neon scrape cache (no live scrape).</summary>
public class LubesItemPreviewTests
{
    private readonly Mock<ICategoryClassifier> _classifier = new();
    private readonly Mock<ISapB1Service> _sap = new(MockBehavior.Strict);
    private readonly Mock<INeonProductRepository> _products = new(MockBehavior.Strict);
    private readonly Mock<INeonLiquiMolyRepository> _lm = new();
    private readonly Mock<ICategoryTaxonomy> _taxonomy = new();

    private LubesItemProvisioningService Service(LiquiMolyProductDto cached)
    {
        _lm.Setup(r => r.GetByArticleNumberAsync(cached.ArticleNumber, It.IsAny<CancellationToken>())).ReturnsAsync(cached);
        return new LubesItemProvisioningService(
            _classifier.Object, Mock.Of<IPricingCalculator>(), _sap.Object, _lm.Object, _products.Object,
            null!, null!, _taxonomy.Object, Options.Create(new PricingSettings()),
            Mock.Of<ILubesPricingRepository>(), NullLogger<LubesItemProvisioningService>.Instance);
    }

    [Fact]
    public async Task Coolant_UsesTheRules_WithoutTheClassifier_AndWritesNothing()
    {
        var svc = Service(new LiquiMolyProductDto
        {
            ArticleNumber = "21130", Name = "Coolant Ready Mix RAF 12 Plus", PackagingSize = "1 l",
        });

        var r = await svc.PreviewAsync(new LubesItemPreviewRequest("21130"), CancellationToken.None);

        Assert.Equal("found", r.Status);
        Assert.Equal("21130-Coolant Ready Mix RAF 12 Plus-1 l", r.ItemName);
        Assert.Equal("__import__.lm_cat_service_products__coolant_antifreeze", r.OdooCategoryExternalId);
        Assert.Equal(120, r.SapItemGroupCode);
        Assert.Equal("rule", r.OdooCategorySource);
        Assert.False(r.NeedsReview);
        _classifier.VerifyNoOtherCalls();
        _lm.Verify(x => x.UpsertAsync(It.IsAny<LiquiMolyProductDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClassifierDown_StillShowsTheProduct_FlaggedForReview()
    {
        _classifier.Setup(c => c.ClassifyCategoryAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new HttpRequestException("down"));
        _classifier.Setup(c => c.ClassifyFamilyAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new HttpRequestException("down"));
        var svc = Service(new LiquiMolyProductDto { ArticleNumber = "23139", Name = "Top Tec 6600 0W-20", PackagingSize = "5 l" });

        var r = await svc.PreviewAsync(new LubesItemPreviewRequest("23139"), CancellationToken.None);

        Assert.Equal("found", r.Status);
        Assert.NotNull(r.Product);
        Assert.Null(r.OdooCategoryExternalId);
        Assert.Null(r.SapItemGroupCode);
        Assert.True(r.NeedsReview);
        Assert.Contains("classifier unavailable", r.ClassificationNote);
        Assert.False(r.ClassificationComplete);
    }

    [Fact]
    public async Task ScrapeOnly_StopsBeforeTheClassifier()
    {
        var svc = Service(new LiquiMolyProductDto { ArticleNumber = "23139", Name = "Top Tec 6600 0W-20", PackagingSize = "5 l" });
        var steps = new List<string>();
        var progress = new Mock<IItemPreviewProgress>();
        progress.Setup(p => p.Step(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
                .Callback<string, string, string, string?>((k, _, st, _) => steps.Add($"{k}:{st}"));

        var r = await svc.PreviewAsync(new LubesItemPreviewRequest("23139", Classify: false), progress.Object, CancellationToken.None);

        Assert.Equal("found", r.Status);
        Assert.Equal("23139-Top Tec 6600 0W-20-5 l", r.ItemName);
        Assert.False(r.ClassificationComplete);
        Assert.DoesNotContain(steps, s => s.StartsWith("category") || s.StartsWith("group"));
        Assert.Contains("scrape:skipped", steps);   // read from the cache
        _classifier.VerifyNoOtherCalls();
        progress.Verify(p => p.Product(It.IsAny<LiquiMolyProductDto>(), "Liqui Moly", r.ItemName!), Times.Once);
    }

    [Fact]
    public async Task Classification_AsksBothAtOnce()
    {
        var bothStarted = new TaskCompletionSource();
        var started = 0;
        async Task Gate() { if (Interlocked.Increment(ref started) == 2) bothStarted.SetResult(); await bothStarted.Task; }
        _classifier.Setup(c => c.ClassifyCategoryAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                   .Returns(async () => { await Gate(); return new CategoryClassification
                       { ExternalId = "__import__.lm_cat_motor_oils__passenger_car", Name = "Passenger car", Confidence = 0.9 }; });
        _classifier.Setup(c => c.ClassifyFamilyAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                   .Returns(async () => { await Gate(); return new FamilyClassification
                       { GroupCode = 105, GroupName = "Engine Oils", Confidence = 0.9 }; });
        var svc = Service(new LiquiMolyProductDto { ArticleNumber = "23139", Name = "Top Tec 6600 0W-20" });

        // sequential calls would never pass the gate: each waits for the other to start
        var r = await svc.PreviewAsync(new LubesItemPreviewRequest("23139"), CancellationToken.None)
                         .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(105, r.SapItemGroupCode);
        Assert.True(r.ClassificationComplete);
    }
}
