using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.PurchaseSync;
using SapOdooMiddleware.Services;
using Xunit;

namespace SapOdooMiddleware.Tests.PurchaseSync;

public class ApInvoicesControllerTests
{
    private readonly Mock<ISapB1Service> _sap = new();
    private readonly ApInvoicesController _controller;

    public ApInvoicesControllerTests()
    {
        _controller = new ApInvoicesController(_sap.Object, Mock.Of<ILogger<ApInvoicesController>>());
    }

    private static ApInvoiceDraftRequest Request() => new()
    {
        OdooOrderId = 10, OdooShipmentId = "A12423210", CardCode = "S00001", InvoiceNumber = "VR000531623",
        InvoiceDate = new DateTime(2026, 8, 25), Currency = "EUR", GrpoDocentry = 61, FreightFc = 3005,
        FreightExpenseCode = 1, InvoiceTotalFc = 73515.88,
        Lines = new() { new() { ItemCode = "2315", Quantity = 3888, UnitPrice = 5.51, Amount = 21422.88 } },
    };

    [Fact]
    public async Task CreateDraft_PassesTheRequestToSapAndReturnsTheDraft()
    {
        _sap.Setup(s => s.CreateApInvoiceDraftAsync(It.IsAny<ApInvoiceDraftRequest>()))
            .ReturnsAsync(new ApInvoiceDraftResult { DocEntry = 501, DocNum = 31, DocTotalFc = 73515.88 });

        var ok = Assert.IsType<OkObjectResult>(await _controller.CreateDraft(Request()));
        var body = Assert.IsType<ApiResponse<ApInvoiceDraftResult>>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal(501, body.Data!.DocEntry);
    }

    [Fact]
    public async Task CreateDraft_InvalidRequest_IsRejectedBeforeSap()
    {
        var request = Request();
        request.InvoiceNumber = "";

        Assert.IsType<BadRequestObjectResult>(await _controller.CreateDraft(request));
        _sap.Verify(s => s.CreateApInvoiceDraftAsync(It.IsAny<ApInvoiceDraftRequest>()), Times.Never);
    }

    [Fact]
    public async Task CreateDraft_AGuard_IsUnprocessable()
    {
        _sap.Setup(s => s.CreateApInvoiceDraftAsync(It.IsAny<ApInvoiceDraftRequest>()))
            .ThrowsAsync(new ApInvoiceGuardException("TOTAL_MISMATCH", "differs"));

        var result = Assert.IsType<UnprocessableEntityObjectResult>(await _controller.CreateDraft(Request()));
        var body = Assert.IsType<ApiResponse<ApInvoiceDraftResult>>(result.Value);
        Assert.False(body.Success);
    }
}
