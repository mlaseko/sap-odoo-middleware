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
            UOdooSoId = "SO0042",
            CardCode = "C10000",
            Lines =
            [
                new SapSalesOrderLineRequest { ItemCode = "ITEM001", Quantity = 10, UnitPrice = 25.50 }
            ]
        };

        var expected = new SapSalesOrderResponse
        {
            DocEntry = 100,
            DocNum = 200,
            UOdooSoId = "SO0042",
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
        Assert.Equal("SO0042", response.Data.UOdooSoId);
        Assert.Equal(50, response.Data.PickListEntry);
    }

    [Fact]
    public void Create_WithDeprecatedOdooSoRef_ResolvedSoIdFallsBack()
    {
        // Arrange: only deprecated field supplied
        var request = new SapSalesOrderRequest
        {
            UOdooSoId = string.Empty,
            OdooSoRef = "SO0099",
            CardCode = "C10000",
            Lines = [new SapSalesOrderLineRequest { ItemCode = "A1", Quantity = 1, UnitPrice = 10 }]
        };

        // Assert: ResolvedSoId falls back to deprecated alias
        Assert.Equal("SO0099", request.ResolvedSoId);
    }

    [Fact]
    public void Create_UOdooSoIdTakesPrecedenceOverOdooSoRef()
    {
        // Arrange: both fields supplied
        var request = new SapSalesOrderRequest
        {
            UOdooSoId = "SO0042",
            OdooSoRef = "SO_OLD",
            CardCode = "C10000",
            Lines = [new SapSalesOrderLineRequest { ItemCode = "A1", Quantity = 1, UnitPrice = 10 }]
        };

        // Assert: UOdooSoId wins
        Assert.Equal("SO0042", request.ResolvedSoId);
    }

    [Fact]
    public void Create_LineWithOptionalUdfFields_MapsCorrectly()
    {
        // Arrange: line with all optional UDF fields
        var line = new SapSalesOrderLineRequest
        {
            ItemCode = "ITEM001",
            Quantity = 5,
            UnitPrice = 100.0,
            UOdooSoLineId = "SOL/0042/1",
            UOdooMoveId = "MOVE/001",
            UOdooDeliveryId = "PICK/001",
            WarehouseCode = "01"
        };

        Assert.Equal("SOL/0042/1", line.UOdooSoLineId);
        Assert.Equal("MOVE/001", line.UOdooMoveId);
        Assert.Equal("PICK/001", line.UOdooDeliveryId);
    }

    [Fact]
    public async Task Create_ServiceThrows_Returns500WithError()
    {
        // Arrange
        var request = new SapSalesOrderRequest
        {
            UOdooSoId = "SO0099",
            CardCode = "C10000",
            Lines = [new SapSalesOrderLineRequest { ItemCode = "A1", Quantity = 1, UnitPrice = 10 }]
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

    [Fact]
    public async Task Update_ValidRequest_ReturnsOkWithSapResponse()
    {
        // Arrange
        const int docEntry = 100;
        var request = new SapSalesOrderRequest
        {
            UOdooSoId = "SO0042",
            CardCode = "C10000",
            Lines =
            [
                new SapSalesOrderLineRequest { ItemCode = "ITEM001", Quantity = 10, UnitPrice = 25.50 }
            ]
        };

        var expected = new SapSalesOrderResponse
        {
            DocEntry = docEntry,
            DocNum = 200,
            UOdooSoId = "SO0042"
        };

        _sapServiceMock
            .Setup(s => s.UpdateSalesOrderAsync(docEntry, request))
            .ReturnsAsync(expected);

        // Act
        var result = await _controller.Update(docEntry, request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapSalesOrderResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(docEntry, response.Data!.DocEntry);
        Assert.Equal(200, response.Data.DocNum);
        Assert.Equal("SO0042", response.Data.UOdooSoId);
    }

    [Fact]
    public async Task Update_ServiceThrows_Returns500WithError()
    {
        // Arrange
        const int docEntry = 999;
        var request = new SapSalesOrderRequest
        {
            UOdooSoId = "SO0099",
            CardCode = "C10000",
            Lines = [new SapSalesOrderLineRequest { ItemCode = "A1", Quantity = 1, UnitPrice = 10 }]
        };

        _sapServiceMock
            .Setup(s => s.UpdateSalesOrderAsync(docEntry, request))
            .ThrowsAsync(new InvalidOperationException($"SAP B1 Sales Order with DocEntry={docEntry} not found."));

        // Act
        var result = await _controller.Update(docEntry, request);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
        var response = Assert.IsType<ApiResponse<SapSalesOrderResponse>>(objectResult.Value);
        Assert.False(response.Success);
        Assert.Contains($"DocEntry={docEntry}", response.Errors!.First());
    }

    [Fact]
    public async Task Create_WithOdooDeliveryId_PassesThroughToService()
    {
        // Arrange: request includes odoo_delivery_id
        var request = new SapSalesOrderRequest
        {
            UOdooSoId = "SO0077",
            CardCode = "C10000",
            OdooDeliveryId = "WH/OUT/00042",
            Lines = [new SapSalesOrderLineRequest { ItemCode = "A1", Quantity = 1, UnitPrice = 10 }]
        };

        var expected = new SapSalesOrderResponse { DocEntry = 1, DocNum = 1, UOdooSoId = "SO0077" };

        _sapServiceMock
            .Setup(s => s.CreateSalesOrderAsync(request))
            .ReturnsAsync(expected);

        // Act
        var result = await _controller.Create(request);

        // Assert: request passes through with OdooDeliveryId set
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("WH/OUT/00042", request.OdooDeliveryId);
        Assert.Equal("WH/OUT/00042", request.ResolvedDeliveryId);
        _sapServiceMock.Verify(s => s.CreateSalesOrderAsync(request), Times.Once);
    }

    [Fact]
    public void ResolvedDeliveryId_OdooDeliveryIdSet_TakesPrecedenceOverNameAndLine()
    {
        var request = new SapSalesOrderRequest
        {
            UOdooSoId = "SO001",
            CardCode = "C10000",
            OdooDeliveryId = "WH/OUT/00099",
            Name = "WH/OUT/00001",
            Lines = [new SapSalesOrderLineRequest { ItemCode = "A1", Quantity = 1, UnitPrice = 10, UOdooDeliveryId = "PICK/001" }]
        };

        Assert.Equal("WH/OUT/00099", request.ResolvedDeliveryId);
    }

    [Fact]
    public void ResolvedDeliveryId_HeaderNameSet_ReturnsHeaderName()
    {
        var request = new SapSalesOrderRequest
        {
            UOdooSoId = "SO001",
            CardCode = "C10000",
            Name = "WH/OUT/00011",
            Lines = [new SapSalesOrderLineRequest { ItemCode = "A1", Quantity = 1, UnitPrice = 10 }]
        };

        Assert.Equal("WH/OUT/00011", request.ResolvedDeliveryId);
    }

    [Fact]
    public void ResolvedDeliveryId_HeaderNameWithSlashes_PreservesSlashes()
    {
        var request = new SapSalesOrderRequest
        {
            UOdooSoId = "SO001",
            CardCode = "C10000",
            Name = "WH/OUT/00099",
            Lines = [new SapSalesOrderLineRequest { ItemCode = "A1", Quantity = 1, UnitPrice = 10, UOdooDeliveryId = "LINE_DEL_1" }]
        };

        // Header-level Name takes precedence over line-level UOdooDeliveryId
        Assert.Equal("WH/OUT/00099", request.ResolvedDeliveryId);
    }

    [Fact]
    public void ResolvedDeliveryId_NoHeaderName_FallsBackToLineDeliveryId()
    {
        var request = new SapSalesOrderRequest
        {
            UOdooSoId = "SO001",
            CardCode = "C10000",
            Lines = [new SapSalesOrderLineRequest { ItemCode = "A1", Quantity = 1, UnitPrice = 10, UOdooDeliveryId = "PICK/001" }]
        };

        Assert.Equal("PICK/001", request.ResolvedDeliveryId);
    }

    [Fact]
    public void ResolvedDeliveryId_NoHeaderNameNoLineDeliveryId_ReturnsNull()
    {
        var request = new SapSalesOrderRequest
        {
            UOdooSoId = "SO001",
            CardCode = "C10000",
            Lines = [new SapSalesOrderLineRequest { ItemCode = "A1", Quantity = 1, UnitPrice = 10 }]
        };

        Assert.Null(request.ResolvedDeliveryId);
    }
}
