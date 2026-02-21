using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

public class SapB1PingControllerTests : IClassFixture<SapB1PingControllerTests.TestAppFactory>
{
    private readonly HttpClient _client;
    private readonly TestAppFactory _factory;

    public SapB1PingControllerTests(TestAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Ping_NoApiKey_Returns401()
    {
        var response = await _client.GetAsync("/api/sapb1/ping");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ping_WrongApiKey_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/sapb1/ping");
        request.Headers.Add("X-Api-Key", "wrong-key");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ping_CorrectApiKey_ServiceSucceeds_Returns200()
    {
        var pingResponse = new SapB1PingResponse
        {
            Connected = true,
            Server = "sql-host",
            CompanyDb = "SBODemoUS",
            LicenseServer = "license-host:30000",
            SldServer = string.Empty
        };

        _factory.SapServiceMock
            .Setup(s => s.PingAsync())
            .ReturnsAsync(pingResponse);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/sapb1/ping");
        request.Headers.Add("X-Api-Key", "test-api-key");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<SapB1PingResponse>>();
        Assert.NotNull(body);
        Assert.True(body!.Success);
        Assert.True(body.Data!.Connected);
        Assert.Equal("sql-host", body.Data.Server);
    }

    [Fact]
    public async Task Ping_CorrectApiKey_ServiceThrows_Returns500()
    {
        _factory.SapServiceMock
            .Setup(s => s.PingAsync())
            .ThrowsAsync(new InvalidOperationException("SAP B1 DI API connection failed (65): Cannot connect"));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/sapb1/ping");
        request.Headers.Add("X-Api-Key", "test-api-key");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<SapB1PingResponse>>();
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
