using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.MonthEnd;
using SapOdooMiddleware.Services.MonthEnd;

namespace SapOdooMiddleware.Tests.MonthEnd;

public class MonthEndControllerTests
{
    private readonly Mock<IMonthEndReader> _reader = new();
    private readonly MonthEndController _controller;

    public MonthEndControllerTests()
    {
        _controller = new MonthEndController(_reader.Object, Mock.Of<ILogger<MonthEndController>>());
    }

    [Fact]
    public async Task SupplierBalances_PassesTheDateAndWrapsTheRows()
    {
        var asOf = new DateOnly(2026, 11, 30);
        _reader.Setup(r => r.ReadSupplierBalancesAsync(asOf, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SupplierBalanceRow> { new() { CardCode = "S00001", Currency = "EUR", BalanceFc = 1200, BalanceLc = 3540000 } });

        var ok = Assert.IsType<OkObjectResult>(await _controller.SupplierBalances("2026-11-30", default));
        var body = Assert.IsType<ApiResponse<SupplierBalancesResponse>>(ok.Value);
        Assert.Equal(asOf, body.Data!.AsOf);
        Assert.Single(body.Data.Rows);
        Assert.Equal("S00001", body.Data.Rows[0].CardCode);
    }

    [Fact]
    public async Task BadDate_Returns400WithoutReading()
    {
        var bad = Assert.IsType<BadRequestObjectResult>(await _controller.Grni("30/11/2026", default));
        Assert.False(((ApiResponse<GrniResponse>)bad.Value!).Success);
        _reader.Verify(r => r.ReadGrniAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Grni_TotalsTheRows()
    {
        _reader.Setup(r => r.ReadGrniAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GrniRow>
            {
                new() { DocEntry = 1, OpenValueLc = 1000.5 },
                new() { DocEntry = 2, OpenValueLc = 250.25 },
            });

        var ok = Assert.IsType<OkObjectResult>(await _controller.Grni(null, default));
        var body = Assert.IsType<ApiResponse<GrniResponse>>(ok.Value);
        Assert.Equal(1250.75, body.Data!.TotalLc);
        Assert.Equal(2, body.Data.Rows.Count);
    }

    [Fact]
    public async Task InventoryValuation_TotalsTheWarehouses()
    {
        _reader.Setup(r => r.ReadInventoryValuationAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WarehouseValueRow> { new() { Warehouse = "01", ValueLc = 100 }, new() { Warehouse = "02", ValueLc = 50 } });

        var ok = Assert.IsType<OkObjectResult>(await _controller.InventoryValuation("2026-11-30", default));
        var body = Assert.IsType<ApiResponse<InventoryValuationResponse>>(ok.Value);
        Assert.Equal(150, body.Data!.TotalLc);
    }

    [Fact]
    public async Task LandedCostCredits_DefaultsToTheMonth_AndReadsCodesAndDocuments()
    {
        DateOnly? seenFrom = null, seenTo = null;
        _reader.Setup(r => r.ReadLandedCostCreditsAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .Callback<DateOnly, DateOnly, CancellationToken>((f, t, _) => { seenFrom = f; seenTo = t; })
            .ReturnsAsync(new List<LandedCostCodeRow> { new() { LcCode = "2", CreditsLc = 1250000 } });
        _reader.Setup(r => r.ReadLandedCostDocumentsAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LandedCostDocumentRow> { new() { DocEntry = 77, OdooShipmentId = "PO-LC-001" } });

        var ok = Assert.IsType<OkObjectResult>(await _controller.LandedCostCredits(null, "2026-11-30", default));
        var body = Assert.IsType<ApiResponse<LandedCostCreditsResponse>>(ok.Value);
        Assert.Equal(new DateOnly(2026, 11, 1), seenFrom);
        Assert.Equal(new DateOnly(2026, 11, 30), seenTo);
        Assert.Single(body.Data!.Codes);
        Assert.Single(body.Data.Documents);

        Assert.IsType<BadRequestObjectResult>(await _controller.LandedCostCredits("2026-12-01", "2026-11-30", default));
    }

    [Fact]
    public async Task SqlFailure_Returns500WithTheMessage()
    {
        _reader.Setup(r => r.ReadSupplierBalancesAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Login failed for user"));

        var status = Assert.IsType<ObjectResult>(await _controller.SupplierBalances(null, default));
        Assert.Equal(500, status.StatusCode);
        Assert.Contains("Login failed", ((ApiResponse<SupplierBalancesResponse>)status.Value!).Errors![0]);
    }
}
