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
/// Posting at current stock. Counting 27 (count date 18 Oct) failed 1470000505:
/// bins counted empty had been sold from after the count date, and SAP deducted the
/// sale a second time. Each count is now carried forward by the stock that moved in
/// and out of its bin since it was counted, and the posting measures against stock now.
/// </summary>
public class AutohubPostAtCurrentStockTests
{
    private const int Counting = 27;
    private static readonly DateTime CountDate = new(2026, 10, 18);

    private static CountingLineDetail Line(int lineNum, decimal system, decimal counted, string item = "", string bin = "003-GF-BSKT06") => new()
    {
        LineNum = lineNum, ItemCode = item == "" ? $"ITEM{lineNum}" : item, WhsCode = "003",
        BinEntry = 500 + lineNum, BinCode = bin,
        SystemQty = system, CountedQty = counted, Counted = true, LineStatus = "O",
    };

    private static CountingLineMovement Move(int lineNum, DateTime at, decimal qtyIn, decimal qtyOut, int transType = 15) => new()
    {
        LineNum = lineNum, TransType = transType, BaseRef = "31756", CreatedAt = at, QtyIn = qtyIn, QtyOut = qtyOut,
    };

    /// <summary>App capture time, in UTC (the tests run the SAP zone as UTC).</summary>
    private static CountingLineCount Captured(int lineNum, decimal counted, DateTime utc, decimal? expected = null) => new()
    {
        LineNum = lineNum, CountedQty = counted, CountedAt = new DateTimeOffset(utc, TimeSpan.Zero), ExpectedUpdatedQty = expected,
    };

    private static AutohubInventoryController Build(
        Mock<IAutohubInventorySqlService> sql, Mock<IAutohubSapB1Service> sap) =>
        new(sql.Object, new Mock<IBinResolver>().Object, sap.Object,
            Options.Create(new AutohubInventorySettings { CountTimeZone = "UTC" }), null!,
            new Mock<IPickMirrorRefreshNotifier>().Object,
            NullLogger<AutohubInventoryController>.Instance);

    private static Mock<IAutohubInventorySqlService> Sql(
        CountingLineDetail[] lines, Dictionary<int, decimal> stockNow, params CountingLineMovement[] movements)
    {
        var sql = new Mock<IAutohubInventorySqlService>();
        sql.Setup(s => s.FindDocEntryByAppRefAsync("OIQR", It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(((int DocEntry, int DocNum)?)null);
        sql.Setup(s => s.GetCountingLinesAsync(Counting, It.IsAny<CancellationToken>())).ReturnsAsync(lines.ToList());
        sql.Setup(s => s.GetCountingCountDateAsync(Counting, It.IsAny<CancellationToken>())).ReturnsAsync(CountDate);
        sql.Setup(s => s.GetCountingStockNowAsync(Counting, It.IsAny<CancellationToken>())).ReturnsAsync(stockNow);
        sql.Setup(s => s.GetCountingMovementsAsync(Counting, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(movements.ToList());
        sql.Setup(s => s.GetWarehouseBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new WarehouseBranch { BplId = 1, BranchName = "Main", Active = true });
        sql.Setup(s => s.GetItemPricesAsync(It.IsAny<IReadOnlyCollection<string>>(), 1, It.IsAny<CancellationToken>()))
           .ReturnsAsync((IReadOnlyCollection<string> codes, int priceList, CancellationToken token) =>
               codes.ToDictionary(c => c, c => 25m, StringComparer.OrdinalIgnoreCase));
        return sql;
    }

    private static Mock<IAutohubSapB1Service> Sap()
    {
        var sap = new Mock<IAutohubSapB1Service>();
        sap.Setup(s => s.CreateInventoryPostingAtCurrentStockAsync(
                It.IsAny<int>(), It.IsAny<List<CountingPostLine>>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new InventoryDocResult { DocEntry = 600, DocNum = 600 });
        return sap;
    }

    private static PostingCreate Request(int[] lineNums, params CountingLineCount[] counts) => new()
    {
        AppRef = "count-27", CountingDocEntry = Counting, LineNums = lineNums.ToList(), LineCounts = counts.ToList(),
    };

    private static string Errors(IActionResult result) =>
        string.Join(" ", ((result as ObjectResult)?.Value as ApiResponse<InventoryDocResult>)?.Errors ?? new List<string>());

    // ── The calculation ───────────────────────────────────────────────

    [Fact]
    public void EntryMoment_ReadsHhmm()
    {
        Assert.Equal(new DateTime(2026, 10, 28, 14, 29, 0), CountingRecalculator.EntryMoment(new DateTime(2026, 10, 28), 1429));
        Assert.Equal(new DateTime(2026, 10, 28, 0, 5, 0), CountingRecalculator.EntryMoment(new DateTime(2026, 10, 28), 5));
        Assert.Equal(new DateTime(2026, 10, 28), CountingRecalculator.EntryMoment(new DateTime(2026, 10, 28), null));
    }

    [Fact]
    public void ReturnAfterTheCount_RaisesTheCount()
    {
        // Counted 6; one piece returned into the bin afterwards; 7 in the bin now.
        var r = CountingRecalculator.Recalculate(Line(0, 6, 6), 6, new DateTime(2026, 10, 20, 10, 0, 0), true, 7,
            new[] { Move(0, new DateTime(2026, 10, 28, 17, 45, 0), 1, 0, 14) });
        Assert.Equal(7, r.UpdatedQty);
        Assert.Equal(1, r.NetMovement);
        Assert.Equal(0, r.Adjustment);
        Assert.Equal(CountingRecalculator.StatusZero, r.Status);
    }

    [Fact]
    public void SaleAfterTheCount_LowersTheCount_AndPostsTheShortage()
    {
        // VAG10497: 9 on the count date, counted 8, one delivered afterwards, 8 now → 7.
        var r = CountingRecalculator.Recalculate(Line(0, 9, 8), 8, new DateTime(2026, 10, 20, 10, 0, 0), true, 8,
            new[] { Move(0, new DateTime(2026, 10, 27, 9, 0, 0), 0, 1) });
        Assert.Equal(7, r.UpdatedQty);
        Assert.Equal(-1, r.Adjustment);
        Assert.Equal(CountingRecalculator.StatusPost, r.Status);
    }

    [Fact]
    public void SaleBeforeTheCount_IsNotDeductedAgain()
    {
        // BM10528: sold at 14:29, counted empty at 16:00, 0 now → nothing to post.
        var r = CountingRecalculator.Recalculate(Line(0, 1, 0), 0, new DateTime(2026, 10, 28, 16, 0, 0), true, 0,
            new[] { Move(0, new DateTime(2026, 10, 28, 14, 29, 0), 0, 1) });
        Assert.Equal(0, r.UpdatedQty);
        Assert.Equal(0, r.Adjustment);
        Assert.Equal(CountingRecalculator.StatusZero, r.Status);
        Assert.Empty(r.Movements);
    }

    [Fact]
    public void MovementsSinceTheCountDate_IncludeThoseBeforeTheCount()
    {
        // BM10528: count date 18 Oct, delivered 28 Oct 14:29, counted empty at 16:00.
        // Nothing is carried forward, but the reviewer still sees the delivery.
        var r = CountingRecalculator.Recalculate(Line(0, 1, 0), 0, new DateTime(2026, 10, 28, 16, 0, 0), true, 0,
            new[]
            {
                Move(0, new DateTime(2026, 10, 17, 9, 0, 0), 5, 0),        // before the count date: ignored
                Move(0, new DateTime(2026, 10, 28, 14, 29, 0), 0, 1),
                Move(1, new DateTime(2026, 10, 28, 14, 29, 0), 0, 3),      // another line
            },
            CountDate);
        Assert.Empty(r.Movements);
        Assert.Equal(CountingRecalculator.StatusZero, r.Status);
        Assert.Equal(1, r.MovedOutSinceCountDate);
        Assert.Equal(0, r.MovedInSinceCountDate);
        Assert.Single(r.MovementsSinceCountDate);
    }

    [Fact]
    public void MoreOutThanCounted_IsAConflict()
    {
        // Counted empty, then one delivered from that bin: the count is doubtful.
        var r = CountingRecalculator.Recalculate(Line(0, 1, 0), 0, new DateTime(2026, 10, 19, 9, 0, 0), true, 0,
            new[] { Move(0, new DateTime(2026, 10, 20, 11, 0, 0), 0, 1) });
        Assert.Equal(-1, r.UpdatedQty);
        Assert.Null(r.Adjustment);
        Assert.Equal(CountingRecalculator.StatusConflict, r.Status);
    }

    [Fact]
    public void MovementInTheSameMinute_CountsAsAfter()
    {
        var r = CountingRecalculator.Recalculate(Line(0, 2, 2), 2, new DateTime(2026, 10, 28, 14, 29, 40), true, 1,
            new[] { Move(0, new DateTime(2026, 10, 28, 14, 29, 0), 0, 1), Move(0, new DateTime(2026, 10, 28, 14, 28, 0), 0, 5) });
        Assert.Equal(1, r.UpdatedQty);
        Assert.Single(r.Movements);
    }

    [Fact]
    public void MeasurePoint_UsesTheCaptureTime_OnlyWhenTheCountsAgree()
    {
        var utc = TimeZoneInfo.Utc;
        var line = Line(0, 1, 3);
        var same = CountingRecalculator.MeasurePoint(line, Captured(0, 3, new DateTime(2026, 10, 19, 8, 30, 0)), CountDate, utc);
        Assert.True(same.Known);
        Assert.Equal(new DateTime(2026, 10, 19, 8, 30, 0), same.From);

        var differs = CountingRecalculator.MeasurePoint(line, Captured(0, 2, new DateTime(2026, 10, 19, 8, 30, 0)), CountDate, utc);
        Assert.False(differs.Known);
        Assert.Equal(CountDate, differs.From);
        Assert.Equal(3, differs.Counted);

        var none = CountingRecalculator.MeasurePoint(line, null, CountDate, utc);
        Assert.False(none.Known);
    }

    [Fact]
    public void MeasurePoint_ConvertsTheCaptureTimeToTheSapZone()
    {
        var eat = TimeZoneInfo.CreateCustomTimeZone("EAT", TimeSpan.FromHours(3), "EAT", "EAT");
        var point = CountingRecalculator.MeasurePoint(Line(0, 1, 1), Captured(0, 1, new DateTime(2026, 10, 19, 8, 30, 0)), CountDate, eat);
        Assert.Equal(new DateTime(2026, 10, 19, 11, 30, 0), point.From);
    }

    // ── The posting ──────────────────────────────────────────────────

    [Fact]
    public async Task PostsUpdatedCounts_AtCurrentStock_AndClosesMatchingLines()
    {
        var lines = new[]
        {
            Line(0, 1, 0, "BM10528"),            // sold before the count → matches now, closes
            Line(1, 9, 8, "VAG10497"),           // sold after the count → 7, posts −1
            Line(2, 4, 3, "BM11922"),            // nothing moved → posts −1 as before
        };
        var stock = new Dictionary<int, decimal> { [0] = 0, [1] = 8, [2] = 4 };
        var sql = Sql(lines, stock,
            Move(0, new DateTime(2026, 10, 28, 14, 29, 0), 0, 1),
            Move(1, new DateTime(2026, 10, 27, 9, 0, 0), 0, 1));
        var sap = Sap();

        var result = await Build(sql, sap).CreatePosting(
            Request(new[] { 0, 1, 2 },
                Captured(0, 0, new DateTime(2026, 10, 28, 16, 0, 0)),
                Captured(1, 8, new DateTime(2026, 10, 20, 10, 0, 0)),
                Captured(2, 3, new DateTime(2026, 10, 20, 10, 0, 0))),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        sap.Verify(s => s.UpdateInventoryCountingLinesAsync(
            Counting,
            It.Is<List<CountingLineUpdate>>(u => u.Count == 1 && u[0].LineNum == 1 && u[0].CountedQty == 7d),
            It.IsAny<List<CountingLineAddition>>(), "003", It.IsAny<CancellationToken>()), Times.Once);
        sap.Verify(s => s.CreateInventoryPostingAtCurrentStockAsync(
            Counting,
            It.Is<List<CountingPostLine>>(l => l.Count == 2
                && l.Single(p => p.LineNum == 1).CountedQty == 7d
                && l.Single(p => p.LineNum == 2).CountedQty == 3d),
            "count-27", It.IsAny<int>(), 1, It.IsAny<int?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        sap.Verify(s => s.CloseInventoryCountingAsync(Counting, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LineWithoutCaptureTime_MeasuresFromTheCountDate()
    {
        // No app capture: the count date (start of day) is the measuring point, so a
        // later sale of a bin counted empty is a conflict, never a negative bin.
        var sql = Sql(new[] { Line(0, 1, 0, "VAG15600", "003-GF-BSKT09") }, new() { [0] = 0 },
            Move(0, new DateTime(2026, 10, 20, 11, 0, 0), 0, 1));
        var sap = Sap();

        var result = await Build(sql, sap).CreatePosting(Request(new[] { 0 }), CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.Contains("VAG15600 in 003-GF-BSKT09", Errors(result));
        sap.Verify(s => s.UpdateInventoryCountingLinesAsync(
            It.IsAny<int>(), It.IsAny<List<CountingLineUpdate>>(), It.IsAny<List<CountingLineAddition>>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        sap.Verify(s => s.CreateInventoryPostingAtCurrentStockAsync(
            It.IsAny<int>(), It.IsAny<List<CountingPostLine>>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StockMovedSinceTheReview_IsRefusedWith409()
    {
        var sql = Sql(new[] { Line(0, 9, 8, "VAG10497") }, new() { [0] = 8 },
            Move(0, new DateTime(2026, 10, 27, 9, 0, 0), 0, 1));
        var sap = Sap();

        var result = await Build(sql, sap).CreatePosting(
            Request(new[] { 0 }, Captured(0, 8, new DateTime(2026, 10, 20, 10, 0, 0), expected: 8)),
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("VAG10497", Errors(result));
        sap.Verify(s => s.CreateInventoryPostingAtCurrentStockAsync(
            It.IsAny<int>(), It.IsAny<List<CountingPostLine>>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefusedPosting_WritesTheOriginalCountsBack()
    {
        var sql = Sql(new[] { Line(0, 9, 8, "VAG10497") }, new() { [0] = 8 },
            Move(0, new DateTime(2026, 10, 27, 9, 0, 0), 0, 1));
        var sap = Sap();
        sap.Setup(s => s.CreateInventoryPostingAtCurrentStockAsync(
                It.IsAny<int>(), It.IsAny<List<CountingPostLine>>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("SAP InventoryPostingsService.Add failed [-10]: 10001484"));

        var result = await Build(sql, sap).CreatePosting(
            Request(new[] { 0 }, Captured(0, 8, new DateTime(2026, 10, 20, 10, 0, 0))), CancellationToken.None);

        Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("10001484", Errors(result));
        sap.Verify(s => s.UpdateInventoryCountingLinesAsync(
            Counting, It.Is<List<CountingLineUpdate>>(u => u.Count == 1 && u[0].CountedQty == 7d),
            It.IsAny<List<CountingLineAddition>>(), "003", It.IsAny<CancellationToken>()), Times.Once);
        sap.Verify(s => s.UpdateInventoryCountingLinesAsync(
            Counting, It.Is<List<CountingLineUpdate>>(u => u.Count == 1 && u[0].CountedQty == 8d),
            It.IsAny<List<CountingLineAddition>>(), "003", It.IsAny<CancellationToken>()), Times.Once);
        // No other route is tried.
        sap.Verify(s => s.CreateDirectInventoryPostingAsync(
            It.IsAny<DirectPostingCreate>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Recalc_ListsEveryCountedOpenLine()
    {
        var sql = Sql(new[] { Line(0, 9, 8), Line(1, 1, 1) }, new() { [0] = 8, [1] = 2 },
            Move(0, new DateTime(2026, 10, 27, 9, 0, 0), 0, 1),
            Move(1, new DateTime(2026, 10, 28, 17, 45, 0), 1, 0, 67));
        var result = await Build(sql, Sap()).RecalculateCounting(Counting, new CountingRecalcRequest
        {
            LineCounts = { Captured(0, 8, new DateTime(2026, 10, 20, 10, 0, 0)) },
        }, CancellationToken.None);

        var body = Assert.IsType<ApiResponse<CountingRecalcResult>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(body.Data!.Enabled);
        Assert.Equal(CountDate, body.Data.CountDate);
        var first = body.Data.Lines.Single(l => l.LineNum == 0);
        Assert.Equal(7, first.UpdatedQty);
        Assert.True(first.CountTimeKnown);
        var second = body.Data.Lines.Single(l => l.LineNum == 1);
        Assert.False(second.CountTimeKnown);       // no capture: from the count date
        Assert.Equal(2, second.UpdatedQty);         // +1 transferred in
        Assert.Equal(0, second.Adjustment);
        Assert.Equal(25m, second.UnitPrice);
        // Since the count date: the first line's delivery (after its count) and the
        // second line's transfer (no capture time, so measured from the count date).
        Assert.Equal(1, first.MovedOutSinceCountDate);
        Assert.Equal(1, second.MovedInSinceCountDate);
        sql.Verify(s => s.GetCountingMovementsAsync(Counting, CountDate, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NegativeStockRefusal_NamesTheItemAndBin()
    {
        // Stock moved between the calculation and SAP's Add: SAP refuses, and the
        // error names the line that would go below zero instead of "line: n".
        var sql = Sql(new[] { Line(0, 4, 3, "BM11922"), Line(1, 2, 1, "BM10528") }, new() { [0] = 4, [1] = 2 });
        var sap = Sap();
        sap.Setup(s => s.CreateInventoryPostingAtCurrentStockAsync(
                It.IsAny<int>(), It.IsAny<List<CountingPostLine>>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
           .Callback(() => sql.Setup(s => s.GetCountingStockNowAsync(Counting, It.IsAny<CancellationToken>()))
                              .ReturnsAsync(new Dictionary<int, decimal> { [0] = 4, [1] = 0 }))
           .ThrowsAsync(new InvalidOperationException(
               "SAP InventoryPostingsService.Add failed [-10]: 1470000505 - Quantity falls into negative inventory [IQR1.Quantity][line: 2]"));

        var result = await Build(sql, sap).CreatePosting(Request(new[] { 0, 1 }), CancellationToken.None);

        Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        var errors = Errors(result);
        Assert.Contains("1470000505", errors);
        Assert.Contains("BM10528 in 003-GF-BSKT06 (stock now 0, change -1)", errors);
        Assert.DoesNotContain("BM11922 in", errors);
    }
}
