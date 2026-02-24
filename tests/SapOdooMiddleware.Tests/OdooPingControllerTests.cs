using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

public class OdooPingControllerTests : IClassFixture<OdooPingControllerTests.TestAppFactory>
{
    private readonly HttpClient _client;
    private readonly TestAppFactory _factory;

    public OdooPingControllerTests(TestAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Ping_NoApiKey_Returns401()
    {
        var response = await _client.GetAsync("/api/odoo/ping");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ping_WrongApiKey_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/odoo/ping");
        request.Headers.Add("X-Api-Key", "wrong-key");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ping_CorrectApiKey_ServiceSucceeds_Returns200()
    {
        var pingResponse = new OdooPingResponse
        {
            Connected = true,
            Uid = 2,
            Database = "mlaseko-molas-lubes",
            ServerVersion = "18.0",
            BaseUrl = "https://mlaseko-molas-lubes.odoo.com",
            UserName = "admin@company.com"
        };

        _factory.OdooServiceMock
            .Setup(s => s.PingAsync())
            .ReturnsAsync(pingResponse);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/odoo/ping");
        request.Headers.Add("X-Api-Key", "test-api-key");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<OdooPingResponse>>();
        Assert.NotNull(body);
        Assert.True(body!.Success);
        Assert.True(body.Data!.Connected);
        Assert.Equal(2, body.Data.Uid);
    }

    [Fact]
    public async Task Ping_CorrectApiKey_ServiceThrows_Returns500()
    {
        _factory.OdooServiceMock
            .Setup(s => s.PingAsync())
            .ThrowsAsync(new InvalidOperationException("Odoo authentication failed — uid is null."));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/odoo/ping");
        request.Headers.Add("X-Api-Key", "test-api-key");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<OdooPingResponse>>();
        Assert.NotNull(body);
        Assert.False(body!.Success);
        Assert.Contains("uid is null", body.Errors!.First());
    }

    public class TestAppFactory : WebApplicationFactory<Program>
    {
        public Mock<IOdooService> OdooServiceMock { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ApiKey:Key", "test-api-key");
            builder.UseSetting("SapB1:Server", "localhost");
            builder.UseSetting("Odoo:BaseUrl", "http://localhost");

            builder.ConfigureServices(services =>
            {
                services.AddSingleton(OdooServiceMock.Object);
                services.AddSingleton<ISapB1Service>(new Mock<ISapB1Service>().Object);
            });
        }
    }
}
