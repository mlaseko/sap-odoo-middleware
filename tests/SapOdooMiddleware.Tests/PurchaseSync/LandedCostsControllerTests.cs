using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.PurchaseSync;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests.PurchaseSync;

public class LandedCostsControllerTests
{
    private readonly Mock<ISapB1Service> _sap = new();
    private readonly LandedCostsController _controller;

    public LandedCostsControllerTests()
    {
        _controller = new LandedCostsController(_sap.Object, Mock.Of<ILogger<LandedCostsController>>());
    }

    private static LandedCostPushRequest Request() => new()
    {
        OdooOrderId = 12,
        OdooShipmentId = "SO-2026-0012",
        ShipRef = "MSKU1234567",
        GrpoDocentry = 345,
        CostLines = new List<LandedCostLineRequest>
        {
            new() { LcCode = "C02", AmountTzs = 1250000 },
            new() { LcCode = "C08", AmountTzs = 180000 },
        },
    };

    [Fact]
    public async Task Create_PassesTheRequestToSapAndReturnsTheResult()
    {
        LandedCostPushRequest? seen = null;
        _sap.Setup(s => s.CreateLandedCostAsync(It.IsAny<LandedCostPushRequest>()))
            .Callback<LandedCostPushRequest>(r => seen = r)
            .ReturnsAsync(new LandedCostPushResult { DocEntry = 77, DocNum = 1007, IdempotencyKey = "SO-2026-0012", ItemLines = 2, CostTotal = 1430000 });

        var result = await _controller.Create(Request());

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<LandedCostPushResult>>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal(77, body.Data!.DocEntry);
        Assert.Equal(1007, body.Data.DocNum);
        Assert.False(body.Data.AlreadyExisted);
        Assert.Equal(1, seen!.PushSequence);
    }

    [Fact]
    public async Task Create_ZeroSequence_IsTreatedAsTheFirstPush()
    {
        var request = Request();
        request.PushSequence = 0;
        _sap.Setup(s => s.CreateLandedCostAsync(request)).ReturnsAsync(new LandedCostPushResult { DocEntry = 1 });

        Assert.IsType<OkObjectResult>(await _controller.Create(request));
        Assert.Equal(1, request.PushSequence);
    }

    [Fact]
    public async Task Create_InvalidShape_Returns400WithoutCallingSap()
    {
        var request = Request();
        request.OdooShipmentId = "";
        request.CostLines.Clear();

        var result = await _controller.Create(request);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var body = Assert.IsType<ApiResponse<LandedCostPushResult>>(bad.Value);
        Assert.False(body.Success);
        Assert.Contains(body.Errors!, e => e.Contains("odoo_shipment_id"));
        Assert.Contains(body.Errors!, e => e.Contains("cost_lines"));
        _sap.Verify(s => s.CreateLandedCostAsync(It.IsAny<LandedCostPushRequest>()), Times.Never);
    }

    [Fact]
    public async Task Create_Guard_Returns422WithTheCode()
    {
        _sap.Setup(s => s.CreateLandedCostAsync(It.IsAny<LandedCostPushRequest>()))
            .ThrowsAsync(new LandedCostGuardException("GRPO_NOT_FOUND", "Goods receipt DocEntry=345 does not exist in SAP (OPDN)."));

        var result = await _controller.Create(Request());

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var body = Assert.IsType<ApiResponse<LandedCostPushResult>>(unprocessable.Value);
        Assert.False(body.Success);
        Assert.StartsWith("GRPO_NOT_FOUND:", body.Errors![0]);
    }

    [Fact]
    public async Task Create_Conflict_Returns409_AndSapError_Returns500()
    {
        _sap.Setup(s => s.CreateLandedCostAsync(It.IsAny<LandedCostPushRequest>()))
            .ThrowsAsync(new SapConflictException("already there"));
        Assert.IsType<ConflictObjectResult>(await _controller.Create(Request()));

        _sap.Setup(s => s.CreateLandedCostAsync(It.IsAny<LandedCostPushRequest>()))
            .ThrowsAsync(new InvalidOperationException("SAP LandedCostsService.AddLandedCost failed [-5002]: no allocation account"));
        var status = Assert.IsType<ObjectResult>(await _controller.Create(Request()));
        Assert.Equal(500, status.StatusCode);
        Assert.Contains("-5002", ((ApiResponse<LandedCostPushResult>)status.Value!).Errors![0]);
    }
}
