using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Services.Odoo;

namespace SapOdooMiddleware.Tests.Controllers;

public class WebhooksControllerTests
{
    private readonly Mock<IOdooJsonRpcClient> _odooClientMock;
    private readonly Mock<ILogger<WebhooksController>> _loggerMock;
    private readonly WebhooksController _controller;

    public WebhooksControllerTests()
    {
        _odooClientMock = new Mock<IOdooJsonRpcClient>();
        _loggerMock = new Mock<ILogger<WebhooksController>>();
        _controller = new WebhooksController(_odooClientMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task DeliveryConfirmation_ValidRequest_Returns200()
    {
        var request = new DeliveryConfirmationRequest
        {
            OdooSoRef = "SO00045",
            SapDeliveryNo = 1234,
            DeliveryDate = "2026-02-20",
            Status = "delivered"
        };

        var expectedResponse = new DeliveryConfirmationResponse
        {
            OdooSoRef = "SO00045",
            SapDeliveryNo = 1234,
            OdooPickingId = 567,
            OdooPickingName = "WH/OUT/00001",
            Status = "done",
            Message = "Delivery confirmed"
        };

        _odooClientMock.Setup(x => x.ConfirmDeliveryAsync(It.IsAny<DeliveryConfirmationRequest>()))
            .ReturnsAsync(expectedResponse);

        var result = await _controller.DeliveryConfirmation(request);

        var objectResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<DeliveryConfirmationResponse>>(objectResult.Value);
        Assert.True(response.Success);
        Assert.Equal("SO00045", response.Data!.OdooSoRef);
        Assert.Equal(1234, response.Data.SapDeliveryNo);
        Assert.Equal("done", response.Data.Status);
    }

    [Fact]
    public async Task DeliveryConfirmation_MissingOdooSoRef_Returns400()
    {
        var request = new DeliveryConfirmationRequest
        {
            OdooSoRef = "",
            SapDeliveryNo = 1234,
            DeliveryDate = "2026-02-20"
        };

        var result = await _controller.DeliveryConfirmation(request);

        var objectResult = Assert.IsType<BadRequestObjectResult>(result);
        var response = Assert.IsType<ApiResponse<object>>(objectResult.Value);
        Assert.False(response.Success);
        Assert.Contains(response.Errors, e => e.Code == "VALIDATION_ERROR");
    }

    [Fact]
    public async Task DeliveryConfirmation_InvalidSapDeliveryNo_Returns400()
    {
        var request = new DeliveryConfirmationRequest
        {
            OdooSoRef = "SO00045",
            SapDeliveryNo = 0,
            DeliveryDate = "2026-02-20"
        };

        var result = await _controller.DeliveryConfirmation(request);

        var objectResult = Assert.IsType<BadRequestObjectResult>(result);
        var response = Assert.IsType<ApiResponse<object>>(objectResult.Value);
        Assert.False(response.Success);
    }

    [Fact]
    public async Task DeliveryConfirmation_MissingDeliveryDate_Returns400()
    {
        var request = new DeliveryConfirmationRequest
        {
            OdooSoRef = "SO00045",
            SapDeliveryNo = 1234,
            DeliveryDate = ""
        };

        var result = await _controller.DeliveryConfirmation(request);

        var objectResult = Assert.IsType<BadRequestObjectResult>(result);
        var response = Assert.IsType<ApiResponse<object>>(objectResult.Value);
        Assert.False(response.Success);
    }

    [Fact]
    public async Task DeliveryConfirmation_OdooError_Returns502()
    {
        var request = new DeliveryConfirmationRequest
        {
            OdooSoRef = "SO00045",
            SapDeliveryNo = 1234,
            DeliveryDate = "2026-02-20"
        };

        _odooClientMock.Setup(x => x.ConfirmDeliveryAsync(It.IsAny<DeliveryConfirmationRequest>()))
            .ThrowsAsync(new InvalidOperationException("Odoo error"));

        var result = await _controller.DeliveryConfirmation(request);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, objectResult.StatusCode);
        var response = Assert.IsType<ApiResponse<object>>(objectResult.Value);
        Assert.False(response.Success);
        Assert.Contains(response.Errors, e => e.Code == "ODOO_CONNECTION_FAILED");
    }

    [Fact]
    public async Task DeliveryConfirmation_HttpError_Returns502()
    {
        var request = new DeliveryConfirmationRequest
        {
            OdooSoRef = "SO00045",
            SapDeliveryNo = 1234,
            DeliveryDate = "2026-02-20"
        };

        _odooClientMock.Setup(x => x.ConfirmDeliveryAsync(It.IsAny<DeliveryConfirmationRequest>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var result = await _controller.DeliveryConfirmation(request);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, objectResult.StatusCode);
    }
}
