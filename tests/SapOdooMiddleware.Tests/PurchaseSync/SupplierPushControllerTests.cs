using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.PurchaseSync;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests.PurchaseSync;

public class SupplierPushControllerTests
{
    private readonly Mock<ISapB1Service> _sap = new();
    private readonly SupplierPushController _controller;

    public SupplierPushControllerTests()
    {
        _controller = new SupplierPushController(_sap.Object, Mock.Of<ILogger<SupplierPushController>>());
    }

    private static SupplierPaymentRequest Payment() => new()
    {
        OdooPaymentId = 123,
        OdooPayRef = "VBNK1/2026/00042",
        CardCode = "S00001",
        DocDate = new DateTime(2026, 11, 3),
        Currency = "eur",
        Rate = 2950.0,
        Amount = 50000.0,
        SapAccountCode = "1026212",
        TransferReference = "SWIFT-001",
        Remarks = "Liqui Moly top-up",
    };

    [Fact]
    public async Task OutgoingPayment_CallsSapAsOutgoingAndReturnsResult()
    {
        SupplierPaymentRequest? seen = null;
        _sap.Setup(s => s.CreateSupplierPaymentOnAccountAsync(It.IsAny<SupplierPaymentRequest>()))
            .Callback<SupplierPaymentRequest>(r => seen = r)
            .ReturnsAsync(new SupplierPaymentResult { DocEntry = 101, DocNum = 2001, ObjType = "46" });

        var result = await _controller.CreateOutgoingPayment(Payment());

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<SupplierPaymentResult>>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal(101, body.Data!.DocEntry);
        Assert.Equal(2001, body.Data.DocNum);
        Assert.False(body.Data.AlreadyExisted);
        Assert.NotNull(seen);
        Assert.False(seen!.IsRefund);
        Assert.Equal("EUR", seen.Currency);
    }

    [Fact]
    public async Task SupplierRefund_CallsSapAsRefund()
    {
        SupplierPaymentRequest? seen = null;
        _sap.Setup(s => s.CreateSupplierPaymentOnAccountAsync(It.IsAny<SupplierPaymentRequest>()))
            .Callback<SupplierPaymentRequest>(r => seen = r)
            .ReturnsAsync(new SupplierPaymentResult { DocEntry = 7, DocNum = 8, ObjType = "24" });

        var result = await _controller.CreateSupplierRefund(Payment());

        Assert.IsType<OkObjectResult>(result);
        Assert.True(seen!.IsRefund);
    }

    [Fact]
    public async Task Payment_MissingFields_Returns400WithoutCallingSap()
    {
        var request = Payment();
        request.CardCode = "";
        request.Amount = 0;
        request.SapAccountCode = " ";

        var result = await _controller.CreateOutgoingPayment(request);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var body = Assert.IsType<ApiResponse<SupplierPaymentResult>>(bad.Value);
        Assert.False(body.Success);
        Assert.Contains(body.Errors!, e => e.Contains("card_code"));
        Assert.Contains(body.Errors!, e => e.Contains("amount"));
        Assert.Contains(body.Errors!, e => e.Contains("sap_account_code"));
        _sap.Verify(s => s.CreateSupplierPaymentOnAccountAsync(It.IsAny<SupplierPaymentRequest>()), Times.Never);
    }

    [Fact]
    public async Task Payment_Conflict_Returns409()
    {
        _sap.Setup(s => s.CreateSupplierPaymentOnAccountAsync(It.IsAny<SupplierPaymentRequest>()))
            .ThrowsAsync(new SapConflictException("was CANCELLED there"));

        var result = await _controller.CreateOutgoingPayment(Payment());

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var body = Assert.IsType<ApiResponse<SupplierPaymentResult>>(conflict.Value);
        Assert.False(body.Success);
    }

    [Fact]
    public async Task Payment_SapError_Returns500WithMessage()
    {
        _sap.Setup(s => s.CreateSupplierPaymentOnAccountAsync(It.IsAny<SupplierPaymentRequest>()))
            .ThrowsAsync(new InvalidOperationException("SAP DI API error -5002: No matching records"));

        var result = await _controller.CreateOutgoingPayment(Payment());

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, status.StatusCode);
        var body = Assert.IsType<ApiResponse<SupplierPaymentResult>>(status.Value);
        Assert.Contains("-5002", body.Errors![0]);
    }

    [Fact]
    public async Task Cancel_PassesDirectionAndMapsAlreadyCancelled()
    {
        _sap.Setup(s => s.CancelSupplierPaymentAsync(101, true))
            .ReturnsAsync(new SapPaymentCancelResponse { DocEntry = 101, DocNum = 2001, AlreadyCancelled = true });

        var result = await _controller.CancelSupplierRefund(101, new SupplierPaymentCancelRequest { OdooPayRef = "X" });

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<SupplierPaymentCancelResult>>(ok.Value);
        Assert.True(body.Data!.Cancelled);
        Assert.True(body.Data.AlreadyCancelled);
        _sap.Verify(s => s.CancelSupplierPaymentAsync(101, true), Times.Once);
    }

    [Fact]
    public async Task Cancel_NotFound_Returns404_AndRefusal_Returns409()
    {
        _sap.Setup(s => s.CancelSupplierPaymentAsync(1, false))
            .ThrowsAsync(new InvalidOperationException("Outgoing Payment DocEntry=1 not found (OVPM)."));
        _sap.Setup(s => s.CancelSupplierPaymentAsync(2, false))
            .ThrowsAsync(new InvalidOperationException("SAP DI API error -10: payment is reconciled"));

        Assert.IsType<NotFoundObjectResult>(await _controller.CancelOutgoingPayment(1, null));
        Assert.IsType<ConflictObjectResult>(await _controller.CancelOutgoingPayment(2, null));
    }

    [Fact]
    public async Task Adjustment_ValidatesDirection_AndPassesThrough()
    {
        var request = new SupplierAdjustmentRequest
        {
            OdooEntryId = 7, OdooRef = "SAE-7", CardCode = "S00001", DocDate = new DateTime(2026, 11, 3),
            Currency = "EUR", Rate = 2950.0, Amount = 25.0, Direction = "sideways", Reason = "bank_charges", SapGlCode = "6011510",
        };

        var bad = Assert.IsType<BadRequestObjectResult>(await _controller.CreateSupplierAdjustment(request));
        Assert.Contains(((ApiResponse<SupplierAdjustmentResult>)bad.Value!).Errors!, e => e.Contains("direction"));

        request.Direction = "credit_supplier";
        _sap.Setup(s => s.CreateSupplierAdjustmentJournalAsync(request))
            .ReturnsAsync(new SupplierAdjustmentResult { JdtDocentry = 5001, Number = 9001 });

        var ok = Assert.IsType<OkObjectResult>(await _controller.CreateSupplierAdjustment(request));
        var body = Assert.IsType<ApiResponse<SupplierAdjustmentResult>>(ok.Value);
        Assert.Equal(5001, body.Data!.JdtDocentry);
    }

    [Fact]
    public async Task Reverse_BuildsReversalRefAndMapsErrors()
    {
        string? seenRef = null;
        _sap.Setup(s => s.ReverseSupplierAdjustmentJournalAsync(5001, It.IsAny<string>()))
            .Callback<int, string>((_, r) => seenRef = r)
            .ReturnsAsync(new AdjustmentReverseResult { OriginalDocentry = 5001, ReversalDocentry = 5002, ReversalNumber = 9002 });

        var ok = Assert.IsType<OkObjectResult>(
            await _controller.ReverseSupplierAdjustment(5001, new AdjustmentReverseRequest { OdooRef = "SAE-7" }));
        Assert.Equal("SAE-7:REV", seenRef);
        var body = Assert.IsType<ApiResponse<AdjustmentReverseResult>>(ok.Value);
        Assert.Equal(5002, body.Data!.ReversalDocentry);

        _sap.Setup(s => s.ReverseSupplierAdjustmentJournalAsync(6, It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Journal entry TransId=6 not found (OJDT)."));
        Assert.IsType<NotFoundObjectResult>(await _controller.ReverseSupplierAdjustment(6, null));

        _sap.Setup(s => s.ReverseSupplierAdjustmentJournalAsync(8, It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("SAP DI API error -1: period closed"));
        Assert.IsType<ConflictObjectResult>(await _controller.ReverseSupplierAdjustment(8, null));
    }
}
