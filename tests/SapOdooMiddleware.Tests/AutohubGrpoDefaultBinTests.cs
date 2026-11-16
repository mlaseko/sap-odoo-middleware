using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Inventory;
using SapOdooMiddleware.Services;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// POST /grpo: an item that has never been in a bin of warehouse 003 is received into the configured default
/// bin (003-SYSTEM-BIN-LOCATION) instead of being refused; items with a known bin keep it; warehouses without
/// a configured default still refuse. Plus the draft status (open / added / closed / deleted).
/// </summary>
public class AutohubGrpoDefaultBinTests
{
    private const string Card = "VEN1011";

    private static (AutohubInventoryController Controller, Mock<IAutohubSapB1Service> Sap) Build(
        string whs, Dictionary<string, BinResolution> resolutionByItem, List<WarehouseBin> bins)
    {
        var poLines = resolutionByItem.Keys.Select((item, i) => new OpenPurchaseOrderLine
        {
            DocEntry = 235, DocNum = 235, CardCode = Card, LineNum = i, ItemCode = item,
            Quantity = 5, OpenQty = 5, WhsCode = whs, DocCurrency = "TZS", DocRate = 1,
        }).ToList();

        var sql = new Mock<IAutohubInventorySqlService>();
        sql.Setup(s => s.FindDocEntryByAppRefAsync("OPDN", It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(((int DocEntry, int DocNum)?)null);
        sql.Setup(s => s.GetOpenPurchaseOrderLinesAsync(Card, null, It.IsAny<CancellationToken>())).ReturnsAsync(poLines);
        sql.Setup(s => s.IsBinManagedAsync(whs, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        sql.Setup(s => s.GetWarehouseBinsAsync(whs, It.IsAny<CancellationToken>())).ReturnsAsync(bins);
        sql.Setup(s => s.GetWarehouseBranchAsync(whs, It.IsAny<CancellationToken>()))
           .ReturnsAsync(new WarehouseBranch { BplId = 1, BranchName = "Main", Active = true });

        var resolver = new Mock<IBinResolver>();
        foreach (var (item, res) in resolutionByItem)
            resolver.Setup(r => r.ResolveAsync(item, whs, BinDirection.Destination, It.IsAny<CancellationToken>())).ReturnsAsync(res);

        var sap = new Mock<IAutohubSapB1Service>();
        sap.Setup(s => s.CreateGrpoAsync(It.IsAny<GrpoCreate>(), It.IsAny<int>(), It.IsAny<PurchaseDocCurrency?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new InventoryDocResult { DocEntry = 2553, DocNum = 2553 });

        var controller = new AutohubInventoryController(sql.Object, resolver.Object, sap.Object,
            Options.Create(new AutohubInventorySettings()), null!, new Mock<IPickMirrorRefreshNotifier>().Object,
            NullLogger<AutohubInventoryController>.Instance);
        return (controller, sap);
    }

    private static GrpoCreate Request(int lineCount) => new()
    {
        AppRef = "test-po235-ctn2", CardCode = Card,
        Lines = Enumerable.Range(0, lineCount).Select(i => new GrpoLineCreate { PoDocEntry = 235, PoLineNum = i, Quantity = 5 }).ToList(),
    };

    private static readonly List<WarehouseBin> Bins003 = new()
    {
        new WarehouseBin { BinAbs = 432, BinCode = "003-GF-BSKT01" },
        new WarehouseBin { BinAbs = 1, BinCode = "003-SYSTEM-BIN-LOCATION", SysBin = true },
    };

    [Fact]
    public async Task ItemWithNoBinInWarehouse003_GoesToTheSystemBin_KnownBinsAreKept()
    {
        var (controller, sap) = Build("003", new()
        {
            ["LR102387"] = new BinResolution { Resolution = "auto", Auto = new BinOption { BinAbs = 432, BinCode = "003-GF-BSKT01" } },
            ["LR102388"] = new BinResolution { Resolution = "required" },
        }, Bins003);

        var result = await controller.CreateGrpo(Request(2), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        sap.Verify(s => s.CreateGrpoAsync(
            It.Is<GrpoCreate>(r => r.Lines[0].BinAbs == 432 && r.Lines[1].BinAbs == 1),
            It.IsAny<int>(), It.IsAny<PurchaseDocCurrency?>(), 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ItemInSeveralBins_StillAsksForAChoice()
    {
        var (controller, sap) = Build("003", new()
        {
            ["LR102388"] = new BinResolution { Resolution = "options", Options = new() { new BinOption { BinAbs = 432 }, new BinOption { BinAbs = 433 } } },
        }, Bins003);

        var result = await controller.CreateGrpo(Request(1), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("resolver: options", Assert.Single(Assert.IsType<ApiResponse<InventoryDocResult>>(bad.Value).Errors!));
        sap.Verify(s => s.CreateGrpoAsync(It.IsAny<GrpoCreate>(), It.IsAny<int>(), It.IsAny<PurchaseDocCurrency?>(),
            It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WarehouseWithoutAConfiguredDefault_StillRefuses()
    {
        var (controller, _) = Build("004", new() { ["LR102388"] = new BinResolution { Resolution = "required" } },
            new List<WarehouseBin> { new() { BinAbs = 9, BinCode = "004-SYSTEM-BIN-LOCATION" } });

        var result = await controller.CreateGrpo(Request(1), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("destination bin required in warehouse 004", Assert.Single(Assert.IsType<ApiResponse<InventoryDocResult>>(bad.Value).Errors!));
    }

    [Fact]
    public async Task ConfiguredDefaultBinMissingFromTheWarehouse_Returns400NamingTheSetting()
    {
        var (controller, _) = Build("003", new() { ["LR102388"] = new BinResolution { Resolution = "required" } },
            new List<WarehouseBin> { new() { BinAbs = 432, BinCode = "003-GF-BSKT01" } });

        var result = await controller.CreateGrpo(Request(1), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("DefaultReceivingBins", Assert.Single(Assert.IsType<ApiResponse<InventoryDocResult>>(bad.Value).Errors!));
    }

    [Theory]
    [InlineData(true, "C", true, "added")]      // draft 222 → A/P invoice 61
    [InlineData(false, null, true, "added")]    // draft gone, invoice found by draftKey
    [InlineData(true, "O", false, "open")]
    [InlineData(true, "C", false, "closed")]
    [InlineData(false, null, false, "deleted")]
    public void DraftStatus(bool draftExists, string? docStatus, bool invoiceExists, string expected) =>
        Assert.Equal(expected, AutohubInventorySqlService.PurchaseInvoiceDraftStatusOf(draftExists, docStatus, invoiceExists));
}
