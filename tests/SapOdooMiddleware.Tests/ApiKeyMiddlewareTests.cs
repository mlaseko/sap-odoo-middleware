using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

public class ApiKeyMiddlewareTests : IClassFixture<ApiKeyMiddlewareTests.TestAppFactory>
{
    private readonly HttpClient _client;

    public ApiKeyMiddlewareTests(TestAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_NoApiKey_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SalesOrders_NoApiKey_Returns401()
    {
        var response = await _client.PostAsync("/api/sales-orders",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SalesOrders_WrongApiKey_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/sales-orders")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Api-Key", "wrong-key");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SalesOrders_CorrectApiKey_PassesThrough()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/sales-orders")
        {
            Content = new StringContent(
                """{"odoo_so_ref":"SO1","card_code":"C1","lines":[{"item_code":"A1","quantity":1}]}""",
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("X-Api-Key", "test-api-key");

        var response = await _client.SendAsync(request);
        // Should not be 401 — the request passes auth and hits the controller
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public class TestAppFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ApiKey:Key", "test-api-key");
            builder.UseSetting("SapB1:Server", "localhost");
            builder.UseSetting("Odoo:BaseUrl", "http://localhost");

            builder.ConfigureServices(services =>
            {
                // Replace real services with mocks
                services.AddSingleton(new Mock<ISapB1Service>().Object);
                services.AddSingleton<IOdooService>(new Mock<IOdooService>().Object);
            });
        }
    }
}
