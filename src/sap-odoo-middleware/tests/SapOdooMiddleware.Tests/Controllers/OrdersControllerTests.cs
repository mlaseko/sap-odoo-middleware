using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Services.Sap;

namespace SapOdooMiddleware.Tests.Controllers;

public class OrdersControllerTests
{
    private readonly Mock<ISapServiceLayerClient> _sapClientMock;
    private readonly Mock<ILogger<OrdersController>> _loggerMock;
    private readonly OrdersController _controller;

    public OrdersControllerTests()
    {
        _sapClientMock = new Mock<ISapServiceLayerClient>();
        _loggerMock = new Mock<ILogger<OrdersController>>();
        _controller = new OrdersController(_sapClientMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task CreateSalesOrder_ValidRequest_Returns201()
    {
        var request = new SalesOrderRequest
        {
            CustomerCardCode = "C00001",
            OdooOrderRef = "SO00045",
            Lines = new List<SalesOrderLineRequest>
            {
                new() { ItemCode = "A00001", Quantity = 10 }
            }
        };

        var expectedResponse = new SalesOrderResponse
        {
            DocEntry = 100,
            DocNum = 200,
            OdooOrderRef = "SO00045",
            CustomerCardCode = "C00001",
            DocDate = "2026-02-20",
            DocTotal = 500m,
            Status = "Open"
        };

        _sapClientMock.Setup(x => x.CreateSalesOrderAsync(It.IsAny<SalesOrderRequest>()))
            .ReturnsAsync(expectedResponse);

        var result = await _controller.CreateSalesOrder(request);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, objectResult.StatusCode);
        var response = Assert.IsType<ApiResponse<SalesOrderResponse>>(objectResult.Value);
        Assert.True(response.Success);
        Assert.Equal(100, response.Data!.DocEntry);
        Assert.Equal(200, response.Data.DocNum);
        Assert.Equal("SO00045", response.Data.OdooOrderRef);
    }

    [Fact]
    public async Task CreateSalesOrder_MissingCustomerCardCode_Returns400()
    {
        var request = new SalesOrderRequest
        {
            CustomerCardCode = "",
            OdooOrderRef = "SO00045",
            Lines = new List<SalesOrderLineRequest>
            {
                new() { ItemCode = "A00001", Quantity = 10 }
            }
        };

        var result = await _controller.CreateSalesOrder(request);

        var objectResult = Assert.IsType<BadRequestObjectResult>(result);
        var response = Assert.IsType<ApiResponse<object>>(objectResult.Value);
        Assert.False(response.Success);
        Assert.Contains(response.Errors, e => e.Code == "VALIDATION_ERROR");
    }

    [Fact]
    public async Task CreateSalesOrder_MissingOdooOrderRef_Returns400()
    {
        var request = new SalesOrderRequest
        {
            CustomerCardCode = "C00001",
            OdooOrderRef = "",
            Lines = new List<SalesOrderLineRequest>
            {
                new() { ItemCode = "A00001", Quantity = 10 }
            }
        };

        var result = await _controller.CreateSalesOrder(request);

        var objectResult = Assert.IsType<BadRequestObjectResult>(result);
        var response = Assert.IsType<ApiResponse<object>>(objectResult.Value);
        Assert.False(response.Success);
    }

    [Fact]
    public async Task CreateSalesOrder_EmptyLines_Returns400()
    {
        var request = new SalesOrderRequest
        {
            CustomerCardCode = "C00001",
            OdooOrderRef = "SO00045",
            Lines = new List<SalesOrderLineRequest>()
        };

        var result = await _controller.CreateSalesOrder(request);

        var objectResult = Assert.IsType<BadRequestObjectResult>(result);
        var response = Assert.IsType<ApiResponse<object>>(objectResult.Value);
        Assert.False(response.Success);
    }

    [Fact]
    public async Task CreateSalesOrder_ZeroQuantity_Returns400()
    {
        var request = new SalesOrderRequest
        {
            CustomerCardCode = "C00001",
            OdooOrderRef = "SO00045",
            Lines = new List<SalesOrderLineRequest>
            {
                new() { ItemCode = "A00001", Quantity = 0 }
            }
        };

        var result = await _controller.CreateSalesOrder(request);

        var objectResult = Assert.IsType<BadRequestObjectResult>(result);
        var response = Assert.IsType<ApiResponse<object>>(objectResult.Value);
        Assert.False(response.Success);
    }

    [Fact]
    public async Task CreateSalesOrder_SapError_Returns502()
    {
        var request = new SalesOrderRequest
        {
            CustomerCardCode = "C00001",
            OdooOrderRef = "SO00045",
            Lines = new List<SalesOrderLineRequest>
            {
                new() { ItemCode = "A00001", Quantity = 10 }
            }
        };

        _sapClientMock.Setup(x => x.CreateSalesOrderAsync(It.IsAny<SalesOrderRequest>()))
            .ThrowsAsync(new HttpRequestException("SAP error"));

        var result = await _controller.CreateSalesOrder(request);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, objectResult.StatusCode);
        var response = Assert.IsType<ApiResponse<object>>(objectResult.Value);
        Assert.False(response.Success);
        Assert.Contains(response.Errors, e => e.Code == "SAP_SL_REQUEST_FAILED");
    }

    [Fact]
    public async Task CreateSalesOrder_MissingItemCode_Returns400()
    {
        var request = new SalesOrderRequest
        {
            CustomerCardCode = "C00001",
            OdooOrderRef = "SO00045",
            Lines = new List<SalesOrderLineRequest>
            {
                new() { ItemCode = "", Quantity = 10 }
            }
        };

        var result = await _controller.CreateSalesOrder(request);

        var objectResult = Assert.IsType<BadRequestObjectResult>(result);
        var response = Assert.IsType<ApiResponse<object>>(objectResult.Value);
        Assert.False(response.Success);
    }
}
