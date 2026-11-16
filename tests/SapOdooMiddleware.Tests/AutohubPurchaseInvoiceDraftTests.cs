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
/// Receive Shipment: POST /api/autohub/inv/purchase-invoice-drafts merges several GRPOs into one
/// A/P invoice draft, and POST /grpo keeps a foreign PO's rate and refuses mixed currencies.
/// </summary>
public class AutohubPurchaseInvoiceDraftTests
{
    private const string Card = "S0001";

    private static AutohubInventoryController Build(Mock<IAutohubInventorySqlService> sql, Mock<IAutohubSapB1Service> sap) =>
        new(sql.Object, new Mock<IBinResolver>().Object, sap.Object,
            Options.Create(new AutohubInventorySettings()), null!,
            new Mock<IPickMirrorRefreshNotifier>().Object,
            NullLogger<AutohubInventoryController>.Instance);

    private static GrpoForInvoice Grpo(int entry, string card = Card, string status = "O", string canceled = "N",
        string cur = "TZS", double rate = 1, int openLines = 2, int? bpl = 1) => new()
    {
        DocEntry = entry, DocNum = entry + 1000, CardCode = card, DocStatus = status, Canceled = canceled,
        DocCurrency = cur, DocRate = rate, BplId = bpl,
        OpenLines = Enumerable.Range(0, openLines).Select(i => new GrpoOpenLine(i, 3 + i)).ToList(),
    };

    private static PurchaseInvoiceDraftCreate Request(params int[] grpos) => new()
    {
        AppRef = "shipment-12-invoice",
        CardCode = Card,
        NumAtCard = "2026INS0342",
        DocDate = new DateTime(2026, 11, 16),
        TaxDate = new DateTime(2026, 9, 27),
        GrpoDocEntries = grpos.ToList(),
    };

    private static Mock<IAutohubInventorySqlService> Sql(
        IEnumerable<GrpoForInvoice> grpos, PurchaseInvoiceDraftResult? existing = null,
        params PurchaseInvoiceConflict[] conflicts)
    {
        var sql = new Mock<IAutohubInventorySqlService>();
        sql.Setup(s => s.FindPurchaseInvoiceDraftByAppRefAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(existing);
        sql.Setup(s => s.GetGrposForInvoiceAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(grpos.ToList());
        sql.Setup(s => s.FindPurchaseInvoicesByNumAtCardAsync(Card, It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(conflicts.ToList());
        return sql;
    }

    private static Mock<IAutohubSapB1Service> Sap()
    {
        var sap = new Mock<IAutohubSapB1Service>();
        sap.Setup(s => s.CreatePurchaseInvoiceDraftAsync(
                It.IsAny<PurchaseInvoiceDraftCreate>(),
                It.IsAny<IReadOnlyList<(int GrpoDocEntry, GrpoOpenLine Line)>>(),
                It.IsAny<PurchaseDocCurrency>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync((PurchaseInvoiceDraftCreate _, IReadOnlyList<(int GrpoDocEntry, GrpoOpenLine Line)> lines,
                          PurchaseDocCurrency cur, int? _, CancellationToken _) => new PurchaseInvoiceDraftResult
           {
               DraftDocEntry = 812, DraftDocNum = 812, LineCount = lines.Count, DocTotal = 41234500, DocCurrency = cur.Currency,
           });
        return sap;
    }

    private static void VerifyNoDraft(Mock<IAutohubSapB1Service> sap) =>
        sap.Verify(s => s.CreatePurchaseInvoiceDraftAsync(
            It.IsAny<PurchaseInvoiceDraftCreate>(), It.IsAny<IReadOnlyList<(int, GrpoOpenLine)>>(),
            It.IsAny<PurchaseDocCurrency>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);

    private static List<string> Errors(IActionResult result) =>
        Assert.IsType<ApiResponse<PurchaseInvoiceDraftResult>>(((ObjectResult)result).Value).Errors!;

    [Fact]
    public async Task Draft_CopiesEveryOpenLineOfEveryGrpo_InRequestOrder()
    {
        var sql = Sql(new[] { Grpo(3302, openLines: 1), Grpo(3301, openLines: 2) });
        var sap = Sap();

        var result = await Build(sql, sap).CreatePurchaseInvoiceDraft(Request(3301, 3302, 3301), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<PurchaseInvoiceDraftResult>>(ok.Value).Data!;
        Assert.Equal(812, data.DraftDocEntry);
        Assert.Equal(3, data.LineCount);
        Assert.Equal("TZS", data.DocCurrency);
        Assert.False(data.AlreadyExisted);
        sap.Verify(s => s.CreatePurchaseInvoiceDraftAsync(
            It.Is<PurchaseInvoiceDraftCreate>(r => r.GrpoDocEntries.SequenceEqual(new[] { 3301, 3302 })),
            It.Is<IReadOnlyList<(int GrpoDocEntry, GrpoOpenLine Line)>>(l =>
                l.Count == 3
                && l[0].GrpoDocEntry == 3301 && l[0].Line.LineNum == 0
                && l[1].GrpoDocEntry == 3301 && l[1].Line.LineNum == 1
                && l[2].GrpoDocEntry == 3302 && l[2].Line.LineNum == 0),
            new PurchaseDocCurrency("TZS", 1), 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Draft_Replay_ReturnsExistingDraft_WithoutTouchingSap()
    {
        var existing = new PurchaseInvoiceDraftResult
        {
            DraftDocEntry = 812, DraftDocNum = 812, LineCount = 351, DocTotal = 100, DocCurrency = "TZS",
        };
        var sql = Sql(new[] { Grpo(3301) }, existing);
        var sap = Sap();

        var result = await Build(sql, sap).CreatePurchaseInvoiceDraft(Request(3301), CancellationToken.None);

        var data = Assert.IsType<ApiResponse<PurchaseInvoiceDraftResult>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        Assert.True(data.AlreadyExisted);
        Assert.Equal(351, data.LineCount);
        VerifyNoDraft(sap);
        sql.Verify(s => s.GetGrposForInvoiceAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Draft_MissingFields_Returns400()
    {
        var sap = Sap();
        var request = Request();
        request.NumAtCard = " ";
        request.AppRef = "";

        var result = await Build(Sql(Array.Empty<GrpoForInvoice>()), sap).CreatePurchaseInvoiceDraft(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        var errors = Errors(result);
        Assert.Contains(errors, e => e.Contains("app_ref"));
        Assert.Contains(errors, e => e.Contains("num_at_card"));
        Assert.Contains(errors, e => e.Contains("grpo_doc_entries"));
        VerifyNoDraft(sap);
    }

    [Fact]
    public async Task Draft_BadGrpos_Returns400_NamingEach_AndCreatesNothing()
    {
        var sql = Sql(new[]
        {
            Grpo(3301),
            Grpo(3302, card: "S0999"),
            Grpo(3303, canceled: "Y"),
            Grpo(3304, status: "C", openLines: 0),
            Grpo(3305, openLines: 0),
        });
        var sap = Sap();

        var result = await Build(sql, sap).CreatePurchaseInvoiceDraft(
            Request(3301, 3302, 3303, 3304, 3305, 3399), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        var errors = Errors(result);
        Assert.Equal(5, errors.Count);
        Assert.Contains(errors, e => e.Contains("GRPO 4302 belongs to supplier S0999"));
        Assert.Contains(errors, e => e.Contains("GRPO 4303 is cancelled"));
        Assert.Contains(errors, e => e.Contains("GRPO 4304 is closed"));
        Assert.Contains(errors, e => e.Contains("GRPO 4305 is closed"));
        Assert.Contains(errors, e => e.Contains("GRPO DocEntry 3399 does not exist"));
        VerifyNoDraft(sap);
    }

    [Fact]
    public async Task Draft_GrpoRatesDiffer_Returns400()
    {
        var sql = Sql(new[] { Grpo(3301, cur: "USD", rate: 2500), Grpo(3302, cur: "USD", rate: 2510) });
        var sap = Sap();

        var result = await Build(sql, sap).CreatePurchaseInvoiceDraft(Request(3301, 3302), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.Single(Errors(result));
        Assert.Contains("GRPO 4301: USD at 2500", error);
        Assert.Contains("GRPO 4302: USD at 2510", error);
        VerifyNoDraft(sap);
    }

    [Fact]
    public async Task Draft_ForeignGrposWithOneRate_PassTheRate()
    {
        var sql = Sql(new[] { Grpo(3301, cur: "USD", rate: 2500), Grpo(3302, cur: "usd", rate: 2500) });
        var sap = Sap();

        var result = await Build(sql, sap).CreatePurchaseInvoiceDraft(Request(3301, 3302), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        sap.Verify(s => s.CreatePurchaseInvoiceDraftAsync(
            It.IsAny<PurchaseInvoiceDraftCreate>(), It.IsAny<IReadOnlyList<(int, GrpoOpenLine)>>(),
            new PurchaseDocCurrency("USD", 2500), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Draft_SupplierInvoiceNumberUsedUnderAnotherAppRef_Returns409()
    {
        var sql = Sql(new[] { Grpo(3301) }, null,
            new PurchaseInvoiceConflict("invoice", 77, 5077, null),
            new PurchaseInvoiceConflict("draft", 80, 80, "other-app-ref"));
        var sap = Sap();

        var result = await Build(sql, sap).CreatePurchaseInvoiceDraft(Request(3301), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var error = Assert.Single(Errors(conflict));
        Assert.Contains("A/P invoice 5077", error);
        Assert.Contains("open A/P invoice draft 80", error);
        VerifyNoDraft(sap);
    }

    [Fact]
    public async Task Draft_SameNumberUnderTheSameAppRef_IsNotAConflict()
    {
        var sql = Sql(new[] { Grpo(3301) }, null,
            new PurchaseInvoiceConflict("invoice", 77, 5077, "shipment-12-invoice"));
        var sap = Sap();

        var result = await Build(sql, sap).CreatePurchaseInvoiceDraft(Request(3301), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Draft_TotalMismatch_Returns422()
    {
        var sql = Sql(new[] { Grpo(3301) });
        var sap = new Mock<IAutohubSapB1Service>();
        sap.Setup(s => s.CreatePurchaseInvoiceDraftAsync(
                It.IsAny<PurchaseInvoiceDraftCreate>(), It.IsAny<IReadOnlyList<(int, GrpoOpenLine)>>(),
                It.IsAny<PurchaseDocCurrency>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new PurchaseInvoiceDraftTotalMismatchException(812, 1000, 999, "TZS", removed: true));
        var request = Request(3301);
        request.ExpectedTotal = 999;

        var result = await Build(sql, sap).CreatePurchaseInvoiceDraft(request, CancellationToken.None);

        var error = Assert.Single(Errors(Assert.IsType<UnprocessableEntityObjectResult>(result)));
        Assert.Contains("1000.00 TZS", error);
        Assert.Contains("999.00 TZS", error);
        Assert.Contains("was deleted", error);
    }

    [Fact]
    public async Task Draft_GrposInDifferentBranches_Returns400()
    {
        var sql = Sql(new[] { Grpo(3301, bpl: 1), Grpo(3302, bpl: 2) });
        var sap = Sap();

        var result = await Build(sql, sap).CreatePurchaseInvoiceDraft(Request(3301, 3302), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("different branches", Assert.Single(Errors(result)));
        VerifyNoDraft(sap);
    }

    [Fact]
    public void GrposSql_HasOneParameterPerGrpo_AndKeepsGrposWithoutOpenLines()
    {
        var (sql, parameters) = AutohubInventorySqlService.BuildGrposForInvoiceSql(new[] { 3301, 3302 });

        Assert.Equal(new[] { ("@g0", 3301), ("@g1", 3302) }, parameters);
        Assert.Contains("WHERE T0.DocEntry IN (@g0, @g1)", sql);
        Assert.Contains("LEFT JOIN PDN1 T1", sql);
    }

    // ── POST /grpo: base PO currency and rate ──

    private static OpenPurchaseOrderLine PoLine(int docEntry, int lineNum, string cur, double rate) => new()
    {
        DocEntry = docEntry, DocNum = docEntry + 700, CardCode = Card, LineNum = lineNum, ItemCode = "BM10001",
        Quantity = 10, OpenQty = 10, WhsCode = "01", DocCurrency = cur, DocRate = rate,
    };

    private static (Mock<IAutohubInventorySqlService> Sql, Mock<IAutohubSapB1Service> Sap) GrpoMocks(params OpenPurchaseOrderLine[] poLines)
    {
        var sql = new Mock<IAutohubInventorySqlService>();
        sql.Setup(s => s.FindDocEntryByAppRefAsync("OPDN", It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(((int DocEntry, int DocNum)?)null);
        sql.Setup(s => s.GetOpenPurchaseOrderLinesAsync(Card, null, It.IsAny<CancellationToken>()))
           .ReturnsAsync(poLines.ToList());
        sql.Setup(s => s.IsBinManagedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        sql.Setup(s => s.GetWarehouseBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new WarehouseBranch { BplId = 1, BranchName = "Main", Active = true });
        var sap = new Mock<IAutohubSapB1Service>();
        sap.Setup(s => s.CreateGrpoAsync(It.IsAny<GrpoCreate>(), It.IsAny<int>(), It.IsAny<PurchaseDocCurrency?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new InventoryDocResult { DocEntry = 3301, DocNum = 3301 });
        return (sql, sap);
    }

    private static GrpoCreate GrpoRequest(params (int Doc, int Line)[] lines) => new()
    {
        AppRef = "shipment-12-6f0c",
        CardCode = Card,
        NumAtCard = "2026INS0342 / Ctn 1, 2",
        Comments = "Receive Shipment #12",
        Lines = lines.Select(l => new GrpoLineCreate { PoDocEntry = l.Doc, PoLineNum = l.Line, Quantity = 1 }).ToList(),
    };

    [Fact]
    public async Task Grpo_ForeignPo_PassesThePoRate()
    {
        var (sql, sap) = GrpoMocks(PoLine(501, 0, "USD", 2500.5), PoLine(501, 1, "USD", 2500.5));

        var result = await Build(sql, sap).CreateGrpo(GrpoRequest((501, 0), (501, 1)), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        sap.Verify(s => s.CreateGrpoAsync(
            It.Is<GrpoCreate>(r => r.NumAtCard == "2026INS0342 / Ctn 1, 2"), It.IsAny<int>(),
            new PurchaseDocCurrency("USD", 2500.5), 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Grpo_WarehouseWithoutBranch_Returns422_AndPostsNothing()
    {
        var (sql, sap) = GrpoMocks(PoLine(501, 0, "TZS", 1));
        sql.Setup(s => s.GetWarehouseBranchAsync("01", It.IsAny<CancellationToken>()))
           .ReturnsAsync((WarehouseBranch?)null);

        var result = await Build(sql, sap).CreateGrpo(GrpoRequest((501, 0)), CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
        sap.Verify(s => s.CreateGrpoAsync(It.IsAny<GrpoCreate>(), It.IsAny<int>(), It.IsAny<PurchaseDocCurrency?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Grpo_PosInDifferentCurrencies_Returns400()
    {
        var (sql, sap) = GrpoMocks(PoLine(501, 0, "USD", 2500), PoLine(502, 0, "TZS", 1));

        var result = await Build(sql, sap).CreateGrpo(GrpoRequest((501, 0), (502, 0)), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.Single(Assert.IsType<ApiResponse<InventoryDocResult>>(bad.Value).Errors!);
        Assert.Contains("PO 1201: USD at 2500", error);
        Assert.Contains("PO 1202: TZS at 1", error);
        sap.Verify(s => s.CreateGrpoAsync(It.IsAny<GrpoCreate>(), It.IsAny<int>(), It.IsAny<PurchaseDocCurrency?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Grpo_Replay_StillReturnsTheOriginalDocument()
    {
        var (sql, sap) = GrpoMocks(PoLine(501, 0, "TZS", 1));
        sql.Setup(s => s.FindDocEntryByAppRefAsync("OPDN", "shipment-12-6f0c", It.IsAny<CancellationToken>()))
           .ReturnsAsync((3301, 4401));

        var result = await Build(sql, sap).CreateGrpo(GrpoRequest((501, 0)), CancellationToken.None);

        var data = Assert.IsType<ApiResponse<InventoryDocResult>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        Assert.True(data.AlreadyExisted);
        Assert.Equal(3301, data.DocEntry);
        Assert.Equal(4401, data.DocNum);
    }
}
