using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

public class CogsJournalsControllerTests
{
    private readonly Mock<IOdooService> _odooServiceMock = new();
    private readonly Mock<ILogger<CogsJournalsController>> _loggerMock = new();
    private readonly CogsJournalsController _controller;

    public CogsJournalsControllerTests()
    {
        _controller = new CogsJournalsController(
            _odooServiceMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task Create_ValidRequest_ReturnsOkWithCreatedAction()
    {
        // Arrange
        var request = new CogsJournalRequest
        {
            DocEntry = 700,
            DocNum = 800,
            Lines =
            [
                new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, UnitCost = 80.0 },
                new CogsJournalLineRequest { LineNum = 1, ItemCode = "ITEM002", Quantity = 3, UnitCost = 40.0 }
            ]
        };

        _odooServiceMock
            .Setup(o => o.CreateOrUpdateCogsJournalAsync(request))
            .ReturnsAsync(new CogsJournalResponse
            {
                SapDocEntry = 700,
                OdooInvoiceId = 42,
                OdooInvoiceName = "INV/2026/00001",
                CogsJournalEntryId = 150,
                Action = "created",
                Hash = "abc123",
                DebitLineCount = 2,
                TotalCogs = 520.0
            });

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<CogsJournalResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal("created", response.Data!.Action);
        Assert.Equal(150, response.Data.CogsJournalEntryId);
        Assert.Equal(520.0, response.Data.TotalCogs);
        Assert.Equal(2, response.Data.DebitLineCount);
        Assert.Equal("INV/2026/00001", response.Data.OdooInvoiceName);
    }

    [Fact]
    public async Task Create_SameHashExists_ReturnsSkipped()
    {
        // Arrange
        var request = new CogsJournalRequest
        {
            DocEntry = 700,
            Lines =
            [
                new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, UnitCost = 80.0 }
            ]
        };

        _odooServiceMock
            .Setup(o => o.CreateOrUpdateCogsJournalAsync(request))
            .ReturnsAsync(new CogsJournalResponse
            {
                SapDocEntry = 700,
                OdooInvoiceId = 42,
                OdooInvoiceName = "INV/2026/00001",
                CogsJournalEntryId = 150,
                Action = "skipped",
                Hash = "abc123",
                DebitLineCount = 1,
                TotalCogs = 400.0
            });

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<CogsJournalResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal("skipped", response.Data!.Action);
    }

    [Fact]
    public async Task Create_InvoiceNotFound_Returns500()
    {
        // Arrange
        var request = new CogsJournalRequest
        {
            DocEntry = 999,
            Lines =
            [
                new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, UnitCost = 80.0 }
            ]
        };

        _odooServiceMock
            .Setup(o => o.CreateOrUpdateCogsJournalAsync(request))
            .ThrowsAsync(new InvalidOperationException(
                "Odoo invoice not found for SAP DocEntry=999."));

        // Act
        var result = await _controller.Create(request);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
        var response = Assert.IsType<ApiResponse<CogsJournalResponse>>(objectResult.Value);
        Assert.False(response.Success);
        Assert.Contains("DocEntry=999", response.Errors!.First());
    }

    [Fact]
    public async Task Create_WithStockSum_ReturnsOk()
    {
        // Arrange: use StockSum instead of UnitCost
        var request = new CogsJournalRequest
        {
            DocEntry = 701,
            Lines =
            [
                new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, StockSum = 400.0 }
            ]
        };

        _odooServiceMock
            .Setup(o => o.CreateOrUpdateCogsJournalAsync(request))
            .ReturnsAsync(new CogsJournalResponse
            {
                SapDocEntry = 701,
                OdooInvoiceId = 43,
                OdooInvoiceName = "INV/2026/00002",
                CogsJournalEntryId = 151,
                Action = "created",
                Hash = "def456",
                DebitLineCount = 1,
                TotalCogs = 400.0
            });

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<CogsJournalResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(400.0, response.Data!.TotalCogs);
    }
}
