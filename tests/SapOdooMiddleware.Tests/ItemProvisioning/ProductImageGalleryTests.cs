using MolasLubes.Infrastructure.Integrations.LiquiMoly;

namespace SapOdooMiddleware.Tests.ItemProvisioning;

/// <summary>The 21130 page as Liqui Moly serves it (Nov 2026): each picture in many sizes, every pack of KFS 33.</summary>
public class ProductImageGalleryTests
{
    private const string Cdn = "https://liquimoly.cloudimg.io/v7/https://www.liqui-moly.com/media/catalog/product/";
    private const string Www = "https://www.liqui-moly.com/media/catalog/product/";
    private const string Front = "21130_K__hlerfrostschutz_KFS_33_1l_e24f.png";
    private const string Back = "21130_K__hlerfrostschutz_KFS_33_1l_50c9.png";
    private const string FiveLitre = "21131_K__hlerfrostschutz_KFS_33_5l_ab5a.png";

    private static readonly string[] Page =
    {
        Www + "2/1/" + Front + "?w=800&h=533&func=bound",
        Www + "2/1/" + Back + "?w=800&h=533&func=bound",
        Www + "2/1/" + FiveLitre + "?w=800&h=533&func=bound",
        Cdn + "cache/37a8bb6f4fed49d0664444ebb1f25e79/2/1/" + Front + "?w=730&h=720",
        Cdn + "cache/d8d7dcf8f4e0f210162c94df45632a2f/2/1/" + Back + "?w=100&h=67",
        Cdn + "cache/d8d7dcf8f4e0f210162c94df45632a2f/2/1/" + FiveLitre + "?w=100&h=67",
        "https://pim.liqui-moly.de/ws/media/article-image/21130?quality=orig",
        "https://pim.liqui-moly.de/ws/media/article-image/21131?quality=orig",
    };

    [Fact]
    public void KeepsThisPackOnce_FullSize_InOrder()
    {
        var g = ProductImageGallery.Clean(Page, "21130");
        Assert.Equal(new[] { Www + "2/1/" + Front, Www + "2/1/" + Back }, g);
    }

    [Fact]
    public void Max_IsRespected()
        => Assert.Single(ProductImageGallery.Clean(Page, "21130", max: 1));

    [Fact]
    public void NoPicturesOfTheArticle_FallsBackToPim_ThenToThePage()
    {
        Assert.Equal(new[] { "https://pim.liqui-moly.de/ws/media/article-image/21130?quality=orig" },
            ProductImageGallery.Clean(Page.Skip(6), "21130"));
        var other = ProductImageGallery.Clean(new[] { Www + "2/1/" + FiveLitre + "?w=100" }, "9999");
        Assert.Equal(new[] { Www + "2/1/" + FiveLitre }, other);
    }

    [Fact]
    public void JunkAndEmpty_AreIgnored()
        => Assert.Empty(ProductImageGallery.Clean(new[] { null, "", "https://example.com/logo.svg" }, "21130"));
}
