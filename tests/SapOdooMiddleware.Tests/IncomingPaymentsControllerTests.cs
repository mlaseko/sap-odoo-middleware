using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

public class IncomingPaymentsControllerTests
{
    private readonly Mock<ISapB1Service> _sapServiceMock = new();
    private readonly Mock<IOdooService> _odooServiceMock = new();
    private readonly Mock<ILogger<IncomingPaymentsController>> _loggerMock = new();
    private readonly IncomingPaymentsController _controller;

    public IncomingPaymentsControllerTests()
    {
        _controller = new IncomingPaymentsController(
            _sapServiceMock.Object,
            _odooServiceMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task Create_CashPayment_ReturnsOkWithPaymentResponse()
    {
        // Arrange: cash payment with a single invoice allocation
        var request = new SapIncomingPaymentRequest
        {
            ExternalPaymentId = "BNK1/2026/00001",
            CustomerCode = "C10000",
            DocDate = new DateTime(2026, 2, 25),
            Currency = "TZS",
            PaymentTotal = 500000.0,
            IsPartial = false,
            JournalCode = "Cash My Company",
            BankOrCashAccountCode = "1026101",
            IsCashPayment = true,
            Lines =
            [
                new SapIncomingPaymentLineRequest
                {
                    SapInvoiceDocEntry = 700,
                    AppliedAmount = 500000.0,
                    OdooInvoiceId = 42
                }
            ]
        };

        var expected = new SapIncomingPaymentResponse
        {
            DocEntry = 1001,
            DocNum = 2001
        };

        _sapServiceMock
            .Setup(s => s.CreateIncomingPaymentAsync(request))
            .ReturnsAsync(expected);

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapIncomingPaymentResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(1001, response.Data!.DocEntry);
        Assert.Equal(2001, response.Data.DocNum);
        Assert.Null(response.Data.OdooWriteBackSuccess);
    }

    [Fact]
    public async Task Create_BankPayment_ReturnsOkWithPaymentResponse()
    {
        // Arrange: bank payment (NMB TZS)
        var request = new SapIncomingPaymentRequest
        {
            ExternalPaymentId = "BNK1/2026/00002",
            CustomerCode = "C20000",
            DocDate = new DateTime(2026, 2, 26),
            Currency = "TZS",
            PaymentTotal = 1200000.0,
            IsPartial = false,
            JournalCode = "NMB TZS",
            BankOrCashAccountCode = "1026217",
            IsCashPayment = false,
            Lines =
            [
                new SapIncomingPaymentLineRequest { SapInvoiceDocEntry = 701, AppliedAmount = 1200000.0 }
            ]
        };

        var expected = new SapIncomingPaymentResponse
        {
            DocEntry = 1002,
            DocNum = 2002
        };

        _sapServiceMock
            .Setup(s => s.CreateIncomingPaymentAsync(request))
            .ReturnsAsync(expected);

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapIncomingPaymentResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(1002, response.Data!.DocEntry);
    }

    [Fact]
    public async Task Create_PartialPayment_MultipleAllocations_ReturnsOk()
    {
        // Arrange: partial payment split across two invoices
        var request = new SapIncomingPaymentRequest
        {
            ExternalPaymentId = "BNK1/2026/00003",
            CustomerCode = "C30000",
            Currency = "TZS",
            PaymentTotal = 300000.0,
            IsPartial = true,
            JournalCode = "NMB TZS",
            BankOrCashAccountCode = "1026217",
            IsCashPayment = false,
            Lines =
            [
                new SapIncomingPaymentLineRequest { SapInvoiceDocEntry = 800, AppliedAmount = 200000.0 },
                new SapIncomingPaymentLineRequest { SapInvoiceDocEntry = 801, AppliedAmount = 100000.0 }
            ]
        };

        var expected = new SapIncomingPaymentResponse
        {
            DocEntry = 1003,
            DocNum = 2003
        };

        _sapServiceMock
            .Setup(s => s.CreateIncomingPaymentAsync(request))
            .ReturnsAsync(expected);

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapIncomingPaymentResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(1003, response.Data!.DocEntry);
    }

    [Fact]
    public async Task Create_ServiceThrows_Returns500WithError()
    {
        // Arrange
        var request = new SapIncomingPaymentRequest
        {
            ExternalPaymentId = "BNK1/2026/00004",
            CustomerCode = "C10000",
            PaymentTotal = 100000.0,
            Lines = [new SapIncomingPaymentLineRequest { SapInvoiceDocEntry = 999, AppliedAmount = 100000.0 }]
        };

        _sapServiceMock
            .Setup(s => s.CreateIncomingPaymentAsync(request))
            .ThrowsAsync(new InvalidOperationException("SAP DI API error -2028: CardCode is required."));

        // Act
        var result = await _controller.Create(request);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
        var response = Assert.IsType<ApiResponse<SapIncomingPaymentResponse>>(objectResult.Value);
        Assert.False(response.Success);
        Assert.Contains("CardCode is required", response.Errors!.First());
    }

    [Fact]
    public async Task Create_WithOdooPaymentId_TriggersWriteBack()
    {
        // Arrange: request includes OdooPaymentId for write-back
        var request = new SapIncomingPaymentRequest
        {
            ExternalPaymentId = "BNK1/2026/00010",
            CustomerCode = "C10000",
            Currency = "TZS",
            PaymentTotal = 750000.0,
            IsPartial = false,
            BankOrCashAccountCode = "1026217",
            IsCashPayment = false,
            OdooPaymentId = 55,
            Lines = [new SapIncomingPaymentLineRequest { SapInvoiceDocEntry = 700, AppliedAmount = 750000.0 }]
        };

        var sapResponse = new SapIncomingPaymentResponse
        {
            DocEntry = 1010,
            DocNum = 2010,
            OdooPaymentId = 55
        };

        _sapServiceMock
            .Setup(s => s.CreateIncomingPaymentAsync(request))
            .ReturnsAsync(sapResponse);

        _odooServiceMock
            .Setup(o => o.UpdateIncomingPaymentAsync(It.Is<IncomingPaymentWriteBackRequest>(r =>
                r.OdooPaymentId == 55 &&
                r.SapDocEntry == 1010 &&
                r.SapDocNum == 2010)))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapIncomingPaymentResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.True(response.Data!.OdooWriteBackSuccess);
        Assert.Null(response.Data.OdooWriteBackError);

        _odooServiceMock.Verify(
            o => o.UpdateIncomingPaymentAsync(It.IsAny<IncomingPaymentWriteBackRequest>()),
            Times.Once);
    }

    [Fact]
    public async Task Create_WithoutOdooPaymentId_SkipsWriteBack()
    {
        // Arrange: no OdooPaymentId — write-back should be skipped
        var request = new SapIncomingPaymentRequest
        {
            ExternalPaymentId = "BNK1/2026/00011",
            CustomerCode = "C10000",
            PaymentTotal = 200000.0,
            Lines = [new SapIncomingPaymentLineRequest { SapInvoiceDocEntry = 700, AppliedAmount = 200000.0 }]
        };

        var sapResponse = new SapIncomingPaymentResponse
        {
            DocEntry = 1011,
            DocNum = 2011
        };

        _sapServiceMock
            .Setup(s => s.CreateIncomingPaymentAsync(request))
            .ReturnsAsync(sapResponse);

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapIncomingPaymentResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Null(response.Data!.OdooWriteBackSuccess);

        _odooServiceMock.Verify(
            o => o.UpdateIncomingPaymentAsync(It.IsAny<IncomingPaymentWriteBackRequest>()),
            Times.Never);
    }

    [Fact]
    public async Task Create_WriteBackFails_StillReturnsOkWithError()
    {
        // Arrange: SAP succeeds but Odoo write-back throws
        var request = new SapIncomingPaymentRequest
        {
            ExternalPaymentId = "BNK1/2026/00012",
            CustomerCode = "C10000",
            PaymentTotal = 300000.0,
            OdooPaymentId = 77,
            Lines = [new SapIncomingPaymentLineRequest { SapInvoiceDocEntry = 700, AppliedAmount = 300000.0 }]
        };

        var sapResponse = new SapIncomingPaymentResponse
        {
            DocEntry = 1012,
            DocNum = 2012,
            OdooPaymentId = 77
        };

        _sapServiceMock
            .Setup(s => s.CreateIncomingPaymentAsync(request))
            .ReturnsAsync(sapResponse);

        _odooServiceMock
            .Setup(o => o.UpdateIncomingPaymentAsync(It.IsAny<IncomingPaymentWriteBackRequest>()))
            .ThrowsAsync(new InvalidOperationException("Odoo RPC error: Access Denied"));

        // Act
        var result = await _controller.Create(request);

        // Assert: overall request still succeeds (SAP payment was created)
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapIncomingPaymentResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(1012, response.Data!.DocEntry);

        // But write-back is flagged as failed
        Assert.False(response.Data.OdooWriteBackSuccess);
        Assert.Contains("Access Denied", response.Data.OdooWriteBackError);
    }

    [Fact]
    public async Task Create_ForexPayment_PassesThroughForexAccountCode()
    {
        // Arrange: cross-currency payment with Forex account
        var request = new SapIncomingPaymentRequest
        {
            ExternalPaymentId = "BNK1/2026/00013",
            CustomerCode = "C10000",
            Currency = "USD",
            PaymentTotal = 1000.0,
            IsPartial = false,
            JournalCode = "CRDB EUR",
            BankOrCashAccountCode = "1026212",
            IsCashPayment = false,
            ForexAccountCode = "1026216",
            Lines = [new SapIncomingPaymentLineRequest { SapInvoiceDocEntry = 702, AppliedAmount = 1000.0 }]
        };

        var expected = new SapIncomingPaymentResponse { DocEntry = 1013, DocNum = 2013 };

        _sapServiceMock
            .Setup(s => s.CreateIncomingPaymentAsync(request))
            .ReturnsAsync(expected);

        // Act
        var result = await _controller.Create(request);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("1026216", request.ForexAccountCode);
        _sapServiceMock.Verify(s => s.CreateIncomingPaymentAsync(request), Times.Once);
    }

    [Fact]
    public void Request_DefaultValues_AreCorrect()
    {
        var request = new SapIncomingPaymentRequest();

        Assert.Equal(string.Empty, request.ExternalPaymentId);
        Assert.Equal(string.Empty, request.CustomerCode);
        Assert.False(request.IsPartial);
        Assert.False(request.IsCashPayment);
        Assert.Empty(request.Lines);
    }

    [Fact]
    public void LineRequest_MapsAllFields()
    {
        var line = new SapIncomingPaymentLineRequest
        {
            SapInvoiceDocEntry = 500,
            AppliedAmount = 250000.0,
            DiscountAmount = 5000.0,
            OdooInvoiceId = 88
        };

        Assert.Equal(500, line.SapInvoiceDocEntry);
        Assert.Equal(250000.0, line.AppliedAmount);
        Assert.Equal(5000.0, line.DiscountAmount);
        Assert.Equal(88, line.OdooInvoiceId);
    }

    [Fact]
    public void Response_OdooWriteBackSuccess_DefaultsToNull()
    {
        var response = new SapIncomingPaymentResponse
        {
            DocEntry = 100,
            DocNum = 200
        };

        Assert.Null(response.OdooWriteBackSuccess);
        Assert.Null(response.OdooWriteBackError);
        Assert.Null(response.OdooPaymentId);
    }

    [Fact]
    public void WriteBackRequest_MapsAllFields()
    {
        var req = new IncomingPaymentWriteBackRequest
        {
            OdooPaymentId = 33,
            SapDocEntry = 1001,
            SapDocNum = 2001
        };

        Assert.Equal(33, req.OdooPaymentId);
        Assert.Equal(1001, req.SapDocEntry);
        Assert.Equal(2001, req.SapDocNum);
    }
}
