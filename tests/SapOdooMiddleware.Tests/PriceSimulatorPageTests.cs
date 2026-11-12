using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Pricing;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

/// <summary>Renders /pricing/simulator end to end (rate repository mocked; nothing touches Neon/SAP).</summary>
public class PriceSimulatorPageTests : IClassFixture<PriceSimulatorPageTests.TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public PriceSimulatorPageTests(TestAppFactory factory) => _factory = factory;

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    private static string Token(string html)
        => Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public async Task Page_renders_and_an_added_item_is_priced()
    {
        var client = Client();
        var get = await client.GetAsync("/pricing/simulator");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var html = await get.Content.ReadAsStringAsync();
        Assert.Contains("Price simulator", html);
        Assert.Contains("3,150", html);

        var post = await client.PostAsync("/pricing/simulator?handler=Add", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html),
            ["inputsJson"] = "",
            ["Form.Reference"] = "NEW-1",
            ["Form.Description"] = "Test drum",
            ["Form.Cost"] = "1000",
            ["Form.Currency"] = "EUR",
            ["Form.Band"] = "Engine Oils",
        }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var page = await post.Content.ReadAsStringAsync();

        var expected = new PricingCalculator().ComputeNetPricesWithTrace(3_150_000m, "Engine Oils");
        Assert.Contains("NEW-1", page);
        Assert.Contains(expected.RetailInclVat.ToString("N0"), page);
        Assert.Contains("Simulated prices (1)", page);
    }

    [Fact]
    public async Task Download_returns_a_workbook_and_template_is_served()
    {
        var client = Client();
        var html = await (await client.GetAsync("/pricing/simulator")).Content.ReadAsStringAsync();
        var json = "[{\"reference\":\"A1\",\"description\":\"Oil\",\"cost\":32.5,\"currency\":\"EUR\",\"band\":\"Engine Oils\"}]";

        var dl = await client.PostAsync("/pricing/simulator?handler=Download", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html),
            ["inputsJson"] = json,
        }));
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        using (var wb = new XLWorkbook(await dl.Content.ReadAsStreamAsync()))
            Assert.Equal("A1", wb.Worksheet(1).Cell(2, 1).GetString());

        var tpl = await client.GetAsync("/pricing/simulator?handler=Template");
        Assert.Equal(HttpStatusCode.OK, tpl.StatusCode);
    }

    [Fact]
    public async Task Api_simulate_prices_items_with_the_api_key()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/pricing/simulate")
        {
            Content = JsonContent.Create(new
            {
                items = new object[]
                {
                    new { reference = "A1", cost = 32.5, currency = "EUR", band = "Engine Oils" },
                    new { reference = "B2", cost = 9.8, sap_group = 107, odoo_category = "Greases / Spray grease" },
                },
            }),
        };
        req.Headers.Add("X-Api-Key", "test-api-key");
        var res = await Client().SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"band\":\"Greases\"", body);
        Assert.Contains("\"incl_vat\"", body);
    }

    public class TestAppFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ApiKey:Key", "test-api-key");
            builder.UseSetting("SapB1:Server", "localhost");
            builder.UseSetting("Odoo:BaseUrl", "http://localhost");

            var repo = new Mock<ILubesPricingRepository>();
            repo.Setup(r => r.GetEffectiveRateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EffectiveRate(3150m, "test", null));
            repo.Setup(r => r.GetRatioOverridesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((new List<BandRatioOverride>(), new List<MaasaiRatioOverride>()));

            builder.ConfigureServices(services =>
            {
                services.AddSingleton(new Mock<IOdooService>().Object);
                services.AddSingleton<ISapB1Service>(new Mock<ISapB1Service>().Object);
                services.AddScoped(_ => repo.Object);
                services.AddScoped(_ => new Mock<ILubesRepriceService>().Object);
            });
        }
    }
}
