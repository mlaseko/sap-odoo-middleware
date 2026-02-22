using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

public class DeliveriesControllerTests
{
    private readonly Mock<IOdooService> _odooServiceMock = new();
    private readonly Mock<ILogger<DeliveriesController>> _loggerMock = new();
    private readonly DeliveriesController _controller;

    public DeliveriesControllerTests()
    {
        _controller = new DeliveriesController(_odooServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task Create_ValidRequest_ReturnsOkWithDeliveryResponse()
    {
        // Arrange
        var request = new DeliveryUpdateRequest
        {
            UOdooSoId = "SO0042",
            SapDeliveryNo = "DN-001",
            DeliveryDate = new DateTime(2025, 1, 15),
            Status = "delivered"
        };

        var expected = new DeliveryUpdateResponse
        {
            UOdooSoId = "SO0042",
            PickingId = 77,
            PickingName = "WH/OUT/00012",
            State = "done",
            SapDeliveryNo = "DN-001"
        };

        _odooServiceMock
            .Setup(s => s.ConfirmDeliveryAsync(request))
            .ReturnsAsync(expected);

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<DeliveryUpdateResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal("SO0042", response.Data!.UOdooSoId);
        Assert.Equal(77, response.Data.PickingId);
        Assert.Equal("WH/OUT/00012", response.Data.PickingName);
        Assert.Equal("done", response.Data.State);
        Assert.Equal("DN-001", response.Data.SapDeliveryNo);
    }

    [Fact]
    public void Create_WithDeprecatedOdooSoRef_ResolvedSoIdFallsBack()
    {
        // Arrange: only deprecated field supplied
        var request = new DeliveryUpdateRequest
        {
            UOdooSoId = string.Empty,
            OdooSoRef = "SO0042",
            SapDeliveryNo = "DN-001"
        };

        // Assert: ResolvedSoId falls back to deprecated alias
        Assert.Equal("SO0042", request.ResolvedSoId);
    }

    [Fact]
    public void Create_UOdooSoIdTakesPrecedenceOverOdooSoRef()
    {
        // Arrange: both fields supplied
        var request = new DeliveryUpdateRequest
        {
            UOdooSoId = "SO0042",
            OdooSoRef = "SO_OLD",
            SapDeliveryNo = "DN-001"
        };

        // Assert: UOdooSoId wins
        Assert.Equal("SO0042", request.ResolvedSoId);
    }

    [Fact]
    public async Task Create_ServiceThrows_Returns500WithError()
    {
        // Arrange
        var request = new DeliveryUpdateRequest
        {
            UOdooSoId = "SO0099",
            SapDeliveryNo = "DN-002"
        };

        _odooServiceMock
            .Setup(s => s.ConfirmDeliveryAsync(request))
            .ThrowsAsync(new InvalidOperationException("Sale order 'SO0099' not found in Odoo."));

        // Act
        var result = await _controller.Create(request);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
        var response = Assert.IsType<ApiResponse<DeliveryUpdateResponse>>(objectResult.Value);
        Assert.False(response.Success);
        Assert.Contains("not found in Odoo", response.Errors!.First());
    }
}
