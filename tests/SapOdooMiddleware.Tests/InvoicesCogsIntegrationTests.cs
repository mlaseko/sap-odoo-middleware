using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Tests for the COGS journal entry creation triggered from the invoice write-back flow.
/// </summary>
public class InvoicesCogsIntegrationTests
{
    private readonly Mock<ISapB1Service> _sapServiceMock = new();
    private readonly Mock<IOdooService> _odooServiceMock = new();
    private readonly Mock<ILogger<InvoicesController>> _loggerMock = new();
    private readonly InvoicesController _controller;

    public InvoicesCogsIntegrationTests()
    {
        _controller = new InvoicesController(
            _sapServiceMock.Object,
            _odooServiceMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task Create_WithOdooInvoiceId_TriggersCogsAfterWriteBack()
    {
        // Arrange
        var request = new SapInvoiceRequest
        {
            ExternalInvoiceId = "INV/2026/00020",
            CustomerCode = "C10000",
            SapDeliveryDocEntry = 500,
            OdooInvoiceId = 42
        };

        var sapResponse = new SapInvoiceResponse
        {
            DocEntry = 700,
            DocNum = 800,
            ExternalInvoiceId = "INV/2026/00020",
            BaseDeliveryDocEntry = 500,
            Lines =
            [
                new SapInvoiceLineResponse { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, GrossBuyPrice = 80.0 },
                new SapInvoiceLineResponse { LineNum = 1, ItemCode = "ITEM002", Quantity = 3, GrossBuyPrice = 40.0 }
            ]
        };

        _sapServiceMock
            .Setup(s => s.CreateInvoiceAsync(request))
            .ReturnsAsync(sapResponse);

        _odooServiceMock
            .Setup(o => o.UpdateInvoiceSapFieldsAsync(It.IsAny<InvoiceWriteBackRequest>()))
            .ReturnsAsync(new InvoiceWriteBackResponse
            {
                OdooInvoiceId = 42,
                SapDocEntry = 700,
                LinesUpdated = 2,
                Success = true
            });

        _odooServiceMock
            .Setup(o => o.CreateOrUpdateCogsJournalAsync(It.Is<CogsJournalRequest>(r =>
                r.DocEntry == 700 &&
                r.DocNum == 800 &&
                r.Lines.Count == 2 &&
                r.Lines[0].ItemCode == "ITEM001" &&
                r.Lines[0].UnitCost == 80.0 &&
                r.Lines[1].ItemCode == "ITEM002" &&
                r.Lines[1].UnitCost == 40.0)))
            .ReturnsAsync(new CogsJournalResponse
            {
                SapDocEntry = 700,
                OdooInvoiceId = 42,
                OdooInvoiceName = "INV/2026/00020",
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
        var response = Assert.IsType<ApiResponse<SapInvoiceResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.True(response.Data!.OdooWriteBackSuccess);
        Assert.Equal("created", response.Data.CogsJournalAction);
        Assert.Equal(150, response.Data.CogsJournalEntryId);

        // Verify both write-back and COGS were called
        _odooServiceMock.Verify(
            o => o.UpdateInvoiceSapFieldsAsync(It.IsAny<InvoiceWriteBackRequest>()),
            Times.Once);
        _odooServiceMock.Verify(
            o => o.CreateOrUpdateCogsJournalAsync(It.IsAny<CogsJournalRequest>()),
            Times.Once);
    }

    [Fact]
    public async Task Create_WithoutOdooInvoiceId_SkipsBothWriteBackAndCogs()
    {
        // Arrange: no OdooInvoiceId — both write-back and COGS should be skipped
        var request = new SapInvoiceRequest
        {
            ExternalInvoiceId = "INV/2026/00021",
            CustomerCode = "C10000",
            SapDeliveryDocEntry = 500
        };

        var sapResponse = new SapInvoiceResponse
        {
            DocEntry = 701,
            DocNum = 801,
            ExternalInvoiceId = "INV/2026/00021",
            BaseDeliveryDocEntry = 500
        };

        _sapServiceMock
            .Setup(s => s.CreateInvoiceAsync(request))
            .ReturnsAsync(sapResponse);

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapInvoiceResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Null(response.Data!.OdooWriteBackSuccess);
        Assert.Null(response.Data.CogsJournalAction);

        _odooServiceMock.Verify(
            o => o.CreateOrUpdateCogsJournalAsync(It.IsAny<CogsJournalRequest>()),
            Times.Never);
    }

    [Fact]
    public async Task Create_CogsFails_StillReturnsOkWithCogsError()
    {
        // Arrange: SAP and write-back succeed, but COGS creation fails
        var request = new SapInvoiceRequest
        {
            ExternalInvoiceId = "INV/2026/00022",
            CustomerCode = "C10000",
            SapDeliveryDocEntry = 500,
            OdooInvoiceId = 99
        };

        var sapResponse = new SapInvoiceResponse
        {
            DocEntry = 702,
            DocNum = 802,
            ExternalInvoiceId = "INV/2026/00022",
            Lines =
            [
                new SapInvoiceLineResponse { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, GrossBuyPrice = 80.0 }
            ]
        };

        _sapServiceMock
            .Setup(s => s.CreateInvoiceAsync(request))
            .ReturnsAsync(sapResponse);

        _odooServiceMock
            .Setup(o => o.UpdateInvoiceSapFieldsAsync(It.IsAny<InvoiceWriteBackRequest>()))
            .ReturnsAsync(new InvoiceWriteBackResponse
            {
                OdooInvoiceId = 99,
                SapDocEntry = 702,
                LinesUpdated = 1,
                Success = true
            });

        _odooServiceMock
            .Setup(o => o.CreateOrUpdateCogsJournalAsync(It.IsAny<CogsJournalRequest>()))
            .ThrowsAsync(new InvalidOperationException("COGS journal ID not configured"));

        // Act
        var result = await _controller.Create(request);

        // Assert: overall request still succeeds
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapInvoiceResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(702, response.Data!.DocEntry);
        Assert.True(response.Data.OdooWriteBackSuccess);

        // COGS is flagged as failed
        Assert.Equal("failed", response.Data.CogsJournalAction);
        Assert.Contains("COGS journal ID not configured", response.Data.CogsJournalError);
    }

    [Fact]
    public async Task Create_NoLines_SkipsCogs()
    {
        // Arrange: SAP response has no lines (edge case)
        var request = new SapInvoiceRequest
        {
            ExternalInvoiceId = "INV/2026/00023",
            CustomerCode = "C10000",
            SapDeliveryDocEntry = 500,
            OdooInvoiceId = 42
        };

        var sapResponse = new SapInvoiceResponse
        {
            DocEntry = 703,
            DocNum = 803,
            ExternalInvoiceId = "INV/2026/00023",
            Lines = [] // no lines
        };

        _sapServiceMock
            .Setup(s => s.CreateInvoiceAsync(request))
            .ReturnsAsync(sapResponse);

        _odooServiceMock
            .Setup(o => o.UpdateInvoiceSapFieldsAsync(It.IsAny<InvoiceWriteBackRequest>()))
            .ReturnsAsync(new InvoiceWriteBackResponse
            {
                OdooInvoiceId = 42,
                SapDocEntry = 703,
                LinesUpdated = 0,
                Success = true
            });

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapInvoiceResponse>>(okResult.Value);
        Assert.True(response.Success);

        // COGS should not be called since there are no lines
        _odooServiceMock.Verify(
            o => o.CreateOrUpdateCogsJournalAsync(It.IsAny<CogsJournalRequest>()),
            Times.Never);
    }
}
