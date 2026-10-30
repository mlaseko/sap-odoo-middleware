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
/// POST /api/autohub/inv/postings sends SAP only the counting lines with a real
/// difference. SAP strips zero-difference lines from an Inventory Posting, so a
/// posting made only of them failed "Specify at least one item" (1470000281);
/// such a session is closed instead.
/// </summary>
public class AutohubCountingPostingTests
{
    private const int Counting = 19;

    private static CountingLineDetail Line(int lineNum, decimal system, decimal counted) => new()
    {
        LineNum = lineNum, ItemCode = $"ITEM{lineNum}", WhsCode = "003",
        SystemQty = system, CountedQty = counted, Counted = true, LineStatus = "O",
    };

    private static PostingCreate Request(params int[] lineNums) => new()
    {
        AppRef = "count-post-test", CountingDocEntry = Counting, LineNums = lineNums.ToList(),
    };

    private static AutohubInventoryController Build(Mock<IAutohubInventorySqlService> sql, Mock<IAutohubSapB1Service> sap) =>
        new(sql.Object, new Mock<IBinResolver>().Object, sap.Object,
            Options.Create(new AutohubInventorySettings()), null!,
            new Mock<IPickMirrorRefreshNotifier>().Object,
            NullLogger<AutohubInventoryController>.Instance);

    private static Mock<IAutohubInventorySqlService> Sql(params CountingLineDetail[] lines)
    {
        var sql = new Mock<IAutohubInventorySqlService>();
        sql.Setup(s => s.FindDocEntryByAppRefAsync("OIQR", It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(((int DocEntry, int DocNum)?)null);
        sql.Setup(s => s.GetCountingLinesAsync(Counting, It.IsAny<CancellationToken>()))
           .ReturnsAsync(lines.ToList());
        sql.Setup(s => s.GetWarehouseBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new WarehouseBranch { BplId = 1, BranchName = "Main", Active = true });
        // Price list 01 (cost) holds 25 for every test item unless a test says otherwise.
        sql.Setup(s => s.GetItemPricesAsync(It.IsAny<IReadOnlyCollection<string>>(), 1, It.IsAny<CancellationToken>()))
           .ReturnsAsync((IReadOnlyCollection<string> codes, int priceList, CancellationToken token) =>
               codes.ToDictionary(c => c, c => 25m, StringComparer.OrdinalIgnoreCase));
        return sql;
    }

    private static Mock<IAutohubSapB1Service> Sap()
    {
        var sap = new Mock<IAutohubSapB1Service>();
        sap.Setup(s => s.CreateInventoryPostingAsync(
                It.IsAny<int>(), It.IsAny<List<CountingPostLine>>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new InventoryDocResult { DocEntry = 500, DocNum = 500 });
        return sap;
    }

    [Fact]
    public async Task AllLinesMatch_ClosesTheSession_WithoutPosting()
    {
        var sql = Sql(Line(0, 1, 1), Line(1, 2, 2));
        var sap = Sap();

        var result = await Build(sql, sap).CreatePosting(Request(0, 1), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<InventoryDocResult>>(ok.Value);
        Assert.True(body.Data!.ClosedWithoutPosting);
        sap.Verify(s => s.CloseInventoryCountingAsync(Counting, It.IsAny<CancellationToken>()), Times.Once);
        sap.Verify(s => s.CreateInventoryPostingAsync(
            It.IsAny<int>(), It.IsAny<List<CountingPostLine>>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MixedLines_PostsOnlyTheDifferences_ThenClosesTheSession()
    {
        var sql = Sql(Line(0, 1, 1), Line(1, 3, 2), Line(2, 0, 4));
        var sap = Sap();

        var result = await Build(sql, sap).CreatePosting(Request(0, 1, 2), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        sap.Verify(s => s.CreateInventoryPostingAsync(
            Counting,
            It.Is<List<CountingPostLine>>(l => l.Count == 2 && l.All(p => p.LineNum != 0)),
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        sap.Verify(s => s.CloseInventoryCountingAsync(Counting, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MatchingSubset_WithOtherLinesOpen_IsRefused_AndNothingCloses()
    {
        var sql = Sql(Line(0, 1, 1), Line(1, 3, 2));
        var sap = Sap();

        var result = await Build(sql, sap).CreatePosting(Request(0), CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
        sap.Verify(s => s.CloseInventoryCountingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PostingLines_ArePricedFromPriceList01()
    {
        var sql = Sql(Line(0, 1, 1), Line(1, 3, 2));
        var sap = Sap();

        await Build(sql, sap).CreatePosting(Request(0, 1), CancellationToken.None);

        sap.Verify(s => s.CreateInventoryPostingAsync(
            Counting, It.Is<List<CountingPostLine>>(l => l.Count == 1 && l[0].Price == 25d),
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ItemWithoutPriceList01Price_IsRefused_AndNothingPosts()
    {
        var sql = Sql(Line(0, 1, 1), Line(1, 3, 2));
        sql.Setup(s => s.GetItemPricesAsync(It.IsAny<IReadOnlyCollection<string>>(), 1, It.IsAny<CancellationToken>()))
           .ReturnsAsync(new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["ITEM1"] = 0m });
        var sap = Sap();

        var result = await Build(sql, sap).CreatePosting(Request(0, 1), CancellationToken.None);

        var refused = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var body = Assert.IsType<ApiResponse<InventoryDocResult>>(refused.Value);
        Assert.Contains("ITEM1", string.Join(" ", body.Errors ?? new List<string>()));
        sap.Verify(s => s.CreateInventoryPostingAsync(
            It.IsAny<int>(), It.IsAny<List<CountingPostLine>>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        sap.Verify(s => s.CloseInventoryCountingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DifferenceSubset_WithOtherLinesOpen_PostsWithoutClosing()
    {
        var sql = Sql(Line(0, 1, 1), Line(1, 3, 2), Line(2, 5, 5));
        var sap = Sap();

        var result = await Build(sql, sap).CreatePosting(Request(0, 1), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        sap.Verify(s => s.CreateInventoryPostingAsync(
            Counting, It.Is<List<CountingPostLine>>(l => l.Count == 1 && l[0].LineNum == 1),
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        sap.Verify(s => s.CloseInventoryCountingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
