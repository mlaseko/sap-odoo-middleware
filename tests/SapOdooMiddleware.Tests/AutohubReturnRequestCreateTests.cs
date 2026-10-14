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
/// POST /api/autohub/inv/return-requests validates lines against only the invoices the request
/// names — never the customer's whole invoice history (which made create time out for the cash
/// customer). Behaviour and messages are otherwise unchanged.
/// </summary>
public class AutohubReturnRequestCreateTests
{
    private const string Card = "CUS001317";

    private static OpenInvoiceLine InvLine(int docEntry, int lineNum, double qty = 2) => new()
    {
        DocEntry = docEntry, DocNum = docEntry, CardCode = Card, LineNum = lineNum,
        ItemCode = "VAG10658", Quantity = qty, Price = 10, WhsCode = "MAINWHSE",
    };

    private static ReturnRequestCreate Request(params (int Doc, int Line)[] lines) => new()
    {
        AppRef = "srr-test",
        CardCode = Card,
        Lines = lines.Select(l => new ReturnRequestLineCreate
        {
            InvoiceDocEntry = l.Doc, InvoiceLineNum = l.Line, Quantity = 1,
        }).ToList(),
    };

    // Only the SQL service and the Autohub SAP service are exercised by create.
    private static AutohubInventoryController Build(Mock<IAutohubInventorySqlService> sql, Mock<IAutohubSapB1Service> sap) =>
        new(sql.Object, new Mock<IBinResolver>().Object, sap.Object,
            Options.Create(new AutohubInventorySettings()), null!,
            new Mock<IPickMirrorRefreshNotifier>().Object,
            NullLogger<AutohubInventoryController>.Instance);

    private static Mock<IAutohubInventorySqlService> Sql(params OpenInvoiceLine[] invoiceLines)
    {
        var sql = new Mock<IAutohubInventorySqlService>();
        sql.Setup(s => s.FindDocEntryByAppRefAsync("ORRR", It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(((int DocEntry, int DocNum)?)null);
        sql.Setup(s => s.GetInvoiceLinesByDocEntriesAsync(Card, It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(invoiceLines.ToList());
        sql.Setup(s => s.GetWarehouseBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new WarehouseBranch { BplId = 1, BranchName = "Main", Active = true });
        return sql;
    }

    [Fact]
    public async Task Create_LooksUpOnlyTheNamedInvoices_NeverTheFullHistory()
    {
        var sql = Sql(InvLine(101, 0), InvLine(101, 1), InvLine(202, 0));
        var sap = new Mock<IAutohubSapB1Service>();
        sap.Setup(s => s.CreateAutohubReturnRequestAsync(
                It.IsAny<ReturnRequestCreate>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new InventoryDocResult { DocEntry = 208, DocNum = 208 });

        var result = await Build(sql, sap).CreateReturnRequest(Request((101, 0), (101, 1), (202, 0)), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        sql.Verify(s => s.GetInvoiceLinesByDocEntriesAsync(
            Card,
            It.Is<IReadOnlyCollection<int>>(d => d.Count == 2 && d.Contains(101) && d.Contains(202)),
            It.IsAny<CancellationToken>()), Times.Once);
        sql.Verify(s => s.GetInvoiceLinesForReturnAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        sap.Verify(s => s.CreateAutohubReturnRequestAsync(
            It.IsAny<ReturnRequestCreate>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_LineNotOnTheNamedInvoice_Returns400_WithUnchangedMessage_AndNoSapWrite()
    {
        var sql = Sql(InvLine(101, 0));   // invoice 101 has no line 5
        var sap = new Mock<IAutohubSapB1Service>();

        var result = await Build(sql, sap).CreateReturnRequest(Request((101, 5)), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var body = Assert.IsType<ApiResponse<InventoryDocResult>>(bad.Value);
        Assert.Contains(body.Errors!, e => e.Contains($"invoice 101 line 5 is not an AR invoice item line of customer {Card}"));
        sap.Verify(s => s.CreateAutohubReturnRequestAsync(
            It.IsAny<ReturnRequestCreate>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_QuantityAboveInvoiced_StillRejected()
    {
        var sql = Sql(InvLine(101, 0, qty: 1));
        var sap = new Mock<IAutohubSapB1Service>();
        var request = Request((101, 0));
        request.Lines[0].Quantity = 3;

        var result = await Build(sql, sap).CreateReturnRequest(request, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var body = Assert.IsType<ApiResponse<InventoryDocResult>>(bad.Value);
        Assert.Contains(body.Errors!, e => e.Contains("exceeds the invoiced quantity"));
    }
}
