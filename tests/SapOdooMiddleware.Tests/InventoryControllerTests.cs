using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

public class InventoryControllerTests : IClassFixture<InventoryControllerTests.TestAppFactory>
{
    // Middleware serializes with SnakeCaseLower; use the same options when reading responses.
    private static readonly JsonSerializerOptions SnakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _client;
    private readonly TestAppFactory _factory;

    public InventoryControllerTests(TestAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetValuationTotal_NoApiKey_Returns401()
    {
        var response = await _client.GetAsync("/api/inventory/valuation/total");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetValuationTotal_WrongApiKey_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/inventory/valuation/total");
        request.Headers.Add("X-Api-Key", "wrong-key");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetValuationTotal_CorrectApiKey_NoDate_Returns200WithTodaysDate()
    {
        _factory.SapServiceMock
            .Setup(s => s.GetInventoryValuationTotalAsync((DateOnly?)null))
            .ReturnsAsync(9_876_543.21m);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/inventory/valuation/total");
        request.Headers.Add("X-Api-Key", "test-api-key");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<InventoryValuationTotalResponse>>(SnakeCaseOptions);
        Assert.NotNull(body);
        Assert.True(body!.Success);
        Assert.NotNull(body.Data);
        Assert.Equal("TZS", body.Data!.Currency);
        Assert.Equal(9_876_543.21m, body.Data.TotalInventoryValueTzs);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), body.Data.AsOfDate);
    }

    [Fact]
    public async Task GetValuationTotal_CorrectApiKey_WithDate_Returns200WithSuppliedDate()
    {
        var asOfDate = new DateOnly(2026, 3, 31);

        _factory.SapServiceMock
            .Setup(s => s.GetInventoryValuationTotalAsync(asOfDate))
            .ReturnsAsync(5_000_000m);

        var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/inventory/valuation/total?as_of_date=2026-03-31");
        request.Headers.Add("X-Api-Key", "test-api-key");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<InventoryValuationTotalResponse>>(SnakeCaseOptions);
        Assert.NotNull(body);
        Assert.True(body!.Success);
        Assert.Equal(asOfDate, body.Data!.AsOfDate);
        Assert.Equal(5_000_000m, body.Data.TotalInventoryValueTzs);
    }

    [Fact]
    public async Task GetValuationTotal_ServiceThrows_Returns500()
    {
        _factory.SapServiceMock
            .Setup(s => s.GetInventoryValuationTotalAsync(It.IsAny<DateOnly?>()))
            .ThrowsAsync(new InvalidOperationException("SAP B1 DI API connection failed (65): Cannot connect"));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/inventory/valuation/total");
        request.Headers.Add("X-Api-Key", "test-api-key");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<InventoryValuationTotalResponse>>(SnakeCaseOptions);
        Assert.NotNull(body);
        Assert.False(body!.Success);
        Assert.Contains("Cannot connect", body.Errors!.First());
    }

    public class TestAppFactory : WebApplicationFactory<Program>
    {
        public Mock<ISapB1Service> SapServiceMock { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ApiKey:Key", "test-api-key");
            builder.UseSetting("SapB1:Server", "localhost");
            builder.UseSetting("Odoo:BaseUrl", "http://localhost");

            builder.ConfigureServices(services =>
            {
                services.AddSingleton(SapServiceMock.Object);
                services.AddSingleton<IOdooService>(new Mock<IOdooService>().Object);
            });
        }
    }
}
