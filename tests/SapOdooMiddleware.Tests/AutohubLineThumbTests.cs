using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

/// <summary>All lines shows a small picture of the part for lines not in SAP yet; clicking opens it full size.</summary>
public class AutohubLineThumbTests
{
    private static readonly Guid DocId = Guid.NewGuid();

    private static PartsReviewLineRow Line(int n, string status, string? picture) => new(
        Guid.NewGuid(), DocId, n, 1, $"A{n}", new List<string>(), $"Part {n}", "VIKA", 1m, "PC", 10m, null, null, false, status,
        null, null, null, null, null, null, null, null, null, null, null, false, null, null, null, false, false, picture);

    public static async Task<string> Render(IReadOnlyList<PartsReviewLineRow> lines)
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
                var docs = new Mock<IStagingPartsDocumentRepository>();
                docs.Setup(r => r.GetByIdAsync(DocId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new StagingPartsDocumentRow(DocId, "f.pdf", "/f", "sha", 1, "extracted", "ok", null,
                        DateTime.UtcNow, DateTime.UtcNow, 1, null, null, "VIKA", "INV1", DateTime.UtcNow, "USD", 1m));
                s.AddSingleton(docs.Object);
                var review = new Mock<IPartsReviewRepository>();
                review.Setup(r => r.ListByDocumentAsync(DocId, It.IsAny<CancellationToken>())).ReturnsAsync(lines.ToList());
                s.AddSingleton(review.Object);
            });
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-api-key");
        var html = await client.GetStringAsync($"/autohub/documents/{DocId}");
        if (Environment.GetEnvironmentVariable("AUTOHUB_DUMP_HTML") is { Length: > 0 } dump) File.WriteAllText(dump, html);
        return html;
    }

    [Fact]
    public async Task Lines_not_in_SAP_show_their_picture_small_and_zoomable()
    {
        var html = await Render(new[]
        {
            Line(1, "pending", "https://img/one.jpg"),
            Line(2, "created", "https://img/two.jpg"),     // in SAP now: no picture
            Line(3, "matched", "https://img/three.jpg"),   // an existing SAP item: no picture
            Line(4, "create_new", null),                   // no picture known
        });
        Assert.Contains("class=\"ln-thumb\" src=\"https://img/one.jpg\"", html);
        Assert.Contains("data-zoom=\"https://img/one.jpg\"", html);
        Assert.DoesNotContain("https://img/two.jpg", html);
        Assert.DoesNotContain("https://img/three.jpg", html);
        Assert.Contains("/js/autohub/pic-zoom.js", html);
    }
}
