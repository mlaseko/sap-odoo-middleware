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
        Assert.Contains("Classifier unavailable", r.ClassificationNote);
    }
}
