using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// The line detail endpoint adds picture_url: the picture of the part the line points to now (its catalogue
/// row), else the stored image, else the first stored alternative.
/// </summary>
public class AutohubLinePictureTests
{
    private static readonly Guid DocId = Guid.NewGuid();
    private static readonly Guid LineId = Guid.NewGuid();

    private static async Task<JsonNode?> Fetch(string payload, string? catalogueImage)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ApiKey:Key", "test-api-key");
            b.UseSetting("SapB1:Server", "localhost");
            b.UseSetting("Odoo:BaseUrl", "http://localhost");
            b.ConfigureServices(s =>
            {
                s.AddSingleton(new Mock<ISapB1Service>().Object);
                s.AddSingleton<IOdooService>(new Mock<IOdooService>().Object);
                var review = new Mock<IPartsReviewRepository>();
                review.Setup(r => r.GetByIdAsync(LineId, It.IsAny<CancellationToken>())).ReturnsAsync(new PartsReviewLineRow(
                    LineId, DocId, 1, 1, "A", new List<string>(), "d", "VIKA", 1m, "PC", 10m, null, null, false, "pending",
                    null, null, null, null, null, null, null, null, null, null, null, false, null, null, null, false, false));
                review.Setup(r => r.GetEnrichmentPayloadAsync(LineId, It.IsAny<CancellationToken>())).ReturnsAsync(payload);
                s.AddSingleton(review.Object);
                var bridge = new Mock<INeonBridgeService>();
                bridge.Setup(x => x.GetOitmImageUrlAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(catalogueImage);
                s.AddSingleton(bridge.Object);
            });
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-api-key");
        var json = await client.GetStringAsync($"/api/autohub/documents/{DocId}/lines/{LineId}/enrichment");
        return JsonNode.Parse(json);
    }

    [Fact]
    public async Task The_current_parts_catalogue_picture_comes_first()
    {
        var r = await Fetch("""{"neon_oitm_id":42,"item_data":{"image_url":"https://old/part.jpg"}}""", "https://cat/new.jpg");
        Assert.Equal("https://cat/new.jpg", (string?)r!["picture_url"]);
        Assert.Equal("https://old/part.jpg", (string?)r["item_data"]!["image_url"]);   // payload kept as stored
    }

    [Fact]
    public async Task Falls_back_to_the_stored_image_then_the_alternatives()
    {
        var stored = await Fetch("""{"neon_oitm_id":42,"item_data":{"image_url":"https://stored.jpg"}}""", null);
        Assert.Equal("https://stored.jpg", (string?)stored!["picture_url"]);

        var alt = await Fetch("""{"item_data":{"image_url":"","all_image_urls":"https://a.jpg, https://b.jpg"}}""", null);
        Assert.Equal("https://a.jpg", (string?)alt!["picture_url"]);

        var altJson = await Fetch("""{"item_data":{"all_image_urls":"[\"https://c.jpg\"]"}}""", null);
        Assert.Equal("https://c.jpg", (string?)altJson!["picture_url"]);

        var none = await Fetch("""{"item_data":{"primary_description":"x"}}""", null);
        Assert.Null((string?)none!["picture_url"]);
    }
}
