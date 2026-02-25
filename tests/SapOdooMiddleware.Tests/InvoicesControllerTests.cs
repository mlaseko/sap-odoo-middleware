using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

public class InvoicesControllerTests
{
    private readonly Mock<ISapB1Service> _sapServiceMock = new();
    private readonly Mock<ILogger<InvoicesController>> _loggerMock = new();
    private readonly InvoicesController _controller;

    public InvoicesControllerTests()
    {
        _controller = new InvoicesController(_sapServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task Create_CopyFromDelivery_ReturnsOkWithInvoiceResponse()
    {
        // Arrange: invoice copied from delivery
        var request = new SapInvoiceRequest
        {
            ExternalInvoiceId = "INV/2026/00001",
            CustomerCode = "C10000",
            SapDeliveryDocEntry = 500,
            SapSalesOrderDocEntry = 100,
            UOdooSoId = "SO0042",
            DocDate = new DateTime(2026, 2, 25),
            DueDate = new DateTime(2026, 3, 25)
        };

        var expected = new SapInvoiceResponse
        {
            DocEntry = 700,
            DocNum = 800,
            ExternalInvoiceId = "INV/2026/00001",
            BaseDeliveryDocEntry = 500,
            BaseSalesOrderDocEntry = 100
        };

        _sapServiceMock
            .Setup(s => s.CreateInvoiceAsync(request))
            .ReturnsAsync(expected);

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapInvoiceResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(700, response.Data!.DocEntry);
        Assert.Equal(800, response.Data.DocNum);
        Assert.Equal("INV/2026/00001", response.Data.ExternalInvoiceId);
        Assert.Equal(500, response.Data.BaseDeliveryDocEntry);
        Assert.Equal(100, response.Data.BaseSalesOrderDocEntry);
    }

    [Fact]
    public async Task Create_ManualWithLines_ReturnsOkWithInvoiceResponse()
    {
        // Arrange: manual invoice with explicit lines (no delivery copy)
        var request = new SapInvoiceRequest
        {
            ExternalInvoiceId = "INV/2026/00002",
            CustomerCode = "C20000",
            DocDate = new DateTime(2026, 2, 25),
            Lines =
            [
                new SapInvoiceLineRequest { ItemCode = "ITEM001", Quantity = 5, Price = 100.0 },
                new SapInvoiceLineRequest { ItemCode = "ITEM002", Quantity = 3, Price = 50.0, DiscountPercent = 10 }
            ]
        };

        var expected = new SapInvoiceResponse
        {
            DocEntry = 701,
            DocNum = 801,
            ExternalInvoiceId = "INV/2026/00002"
        };

        _sapServiceMock
            .Setup(s => s.CreateInvoiceAsync(request))
            .ReturnsAsync(expected);

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapInvoiceResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(701, response.Data!.DocEntry);
        Assert.Equal("INV/2026/00002", response.Data.ExternalInvoiceId);
        Assert.Null(response.Data.BaseDeliveryDocEntry);
    }

    [Fact]
    public async Task Create_ServiceThrows_Returns500WithError()
    {
        // Arrange
        var request = new SapInvoiceRequest
        {
            ExternalInvoiceId = "INV/2026/00003",
            CustomerCode = "C10000",
            SapDeliveryDocEntry = 999
        };

        _sapServiceMock
            .Setup(s => s.CreateInvoiceAsync(request))
            .ThrowsAsync(new InvalidOperationException(
                "SAP B1 Delivery Note with DocEntry=999 not found."));

        // Act
        var result = await _controller.Create(request);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
        var response = Assert.IsType<ApiResponse<SapInvoiceResponse>>(objectResult.Value);
        Assert.False(response.Success);
        Assert.Contains("DocEntry=999", response.Errors!.First());
    }

    [Fact]
    public void CopyFromDelivery_WithDeliveryDocEntry_ReturnsTrue()
    {
        var request = new SapInvoiceRequest
        {
            ExternalInvoiceId = "INV/2026/00001",
            CustomerCode = "C10000",
            SapDeliveryDocEntry = 500
        };

        Assert.True(request.CopyFromDelivery);
    }

    [Fact]
    public void CopyFromDelivery_WithoutDeliveryDocEntry_ReturnsFalse()
    {
        var request = new SapInvoiceRequest
        {
            ExternalInvoiceId = "INV/2026/00001",
            CustomerCode = "C10000"
        };

        Assert.False(request.CopyFromDelivery);
    }

    [Fact]
    public void CopyFromDelivery_WithZeroDeliveryDocEntry_ReturnsFalse()
    {
        var request = new SapInvoiceRequest
        {
            ExternalInvoiceId = "INV/2026/00001",
            CustomerCode = "C10000",
            SapDeliveryDocEntry = 0
        };

        Assert.False(request.CopyFromDelivery);
    }

    [Fact]
    public async Task Create_WithCurrency_PassesThroughToService()
    {
        // Arrange
        var request = new SapInvoiceRequest
        {
            ExternalInvoiceId = "INV/2026/00004",
            CustomerCode = "C10000",
            Currency = "ZAR",
            SapDeliveryDocEntry = 600,
            DocTotal = 1500.00,
            VatSum = 225.00
        };

        var expected = new SapInvoiceResponse
        {
            DocEntry = 702,
            DocNum = 802,
            ExternalInvoiceId = "INV/2026/00004",
            BaseDeliveryDocEntry = 600
        };

        _sapServiceMock
            .Setup(s => s.CreateInvoiceAsync(request))
            .ReturnsAsync(expected);

        // Act
        var result = await _controller.Create(request);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("ZAR", request.Currency);
        _sapServiceMock.Verify(s => s.CreateInvoiceAsync(request), Times.Once);
    }

    [Fact]
    public void LineRequest_WithBaseDeliveryReference_MapsCorrectly()
    {
        var line = new SapInvoiceLineRequest
        {
            ItemCode = "ITEM001",
            Quantity = 10,
            Price = 50.0,
            DiscountPercent = 5.0,
            WarehouseCode = "01",
            BaseDeliveryDocEntry = 500,
            BaseDeliveryLineNum = 0,
            AccountCode = "400000"
        };

        Assert.Equal("ITEM001", line.ItemCode);
        Assert.Equal(10, line.Quantity);
        Assert.Equal(50.0, line.Price);
        Assert.Equal(5.0, line.DiscountPercent);
        Assert.Equal(500, line.BaseDeliveryDocEntry);
        Assert.Equal(0, line.BaseDeliveryLineNum);
        Assert.Equal("400000", line.AccountCode);
    }
}
