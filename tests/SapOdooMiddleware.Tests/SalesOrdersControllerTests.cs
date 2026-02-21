using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

public class SalesOrdersControllerTests
{
    private readonly Mock<ISapB1Service> _sapServiceMock = new();
    private readonly Mock<ILogger<SalesOrdersController>> _loggerMock = new();
    private readonly SalesOrdersController _controller;

    public SalesOrdersControllerTests()
    {
        _controller = new SalesOrdersController(_sapServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task Create_ValidRequest_ReturnsOkWithSapResponse()
    {
        // Arrange
        var request = new SapSalesOrderRequest
        {
            OdooSoRef = "SO0042",
            CardCode = "C10000",
            Lines =
            [
                new SapSalesOrderLineRequest { ItemCode = "ITEM001", Quantity = 10 }
            ]
        };

        var expected = new SapSalesOrderResponse
        {
            DocEntry = 100,
            DocNum = 200,
            OdooSoRef = "SO0042",
            PickListEntry = 50
        };

        _sapServiceMock
            .Setup(s => s.CreateSalesOrderAsync(request))
            .ReturnsAsync(expected);

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapSalesOrderResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(100, response.Data!.DocEntry);
        Assert.Equal(200, response.Data.DocNum);
        Assert.Equal("SO0042", response.Data.OdooSoRef);
        Assert.Equal(50, response.Data.PickListEntry);
    }

    [Fact]
    public async Task Create_ServiceThrows_Returns500WithError()
    {
        // Arrange
        var request = new SapSalesOrderRequest
        {
            OdooSoRef = "SO0099",
            CardCode = "C10000",
            Lines = [new SapSalesOrderLineRequest { ItemCode = "A1", Quantity = 1 }]
        };

        _sapServiceMock
            .Setup(s => s.CreateSalesOrderAsync(request))
            .ThrowsAsync(new InvalidOperationException("SAP DI API error 123: Item not found"));

        // Act
        var result = await _controller.Create(request);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
        var response = Assert.IsType<ApiResponse<SapSalesOrderResponse>>(objectResult.Value);
        Assert.False(response.Success);
        Assert.Contains("SAP DI API error 123", response.Errors!.First());
    }
}
