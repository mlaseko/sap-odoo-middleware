using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Inventory;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Purchase order from a reviewed Autohub invoice: TZS at PL01 (stored for created lines, worked out from
/// this invoice for matched ones), warehouse 003, skipped / promotional / free lines left off, vendor
/// remembered per supplier, one PO per invoice.
/// </summary>
public class AutohubPurchaseOrderServiceTests
{
    private static readonly Guid DocId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static StagingPartsDocumentRow Doc(string status = "reviewed") => new(
        DocId, "inv.pdf", "/x/inv.pdf", "sha", 1, status, null, null, DateTime.UtcNow, DateTime.UtcNow, 1, null, null,
        "VIKA GmbH", "2026INS0342", new DateTime(2026, 9, 27), "EUR", 500m);

    private static AutohubPoSourceLine Line(int n, string status, string? item, decimal qty = 2, decimal? price = 10,
        decimal? disc = null, decimal? pl01 = null, bool promo = false) =>
        new(Guid.NewGuid(), n, status, promo, item, "ART" + n, "Part " + n, "VIKA", qty, price, disc, pl01);

    private sealed class Fixture
    {
        public Mock<IStagingPartsDocumentRepository> Docs = new();
        public Mock<IAutohubPurchaseOrderRepository> Repo = new();
        public Mock<IAutohubInventorySqlService> Sql = new();
        public Mock<IAutohubSapB1Service> Sap = new();
        public Mock<IForexConversionService> Forex = new();
        public Mock<IPricingCalculationService> Pricing = new();
        public Mock<IInvoiceActivityLog> Activity = new();
        public SapPurchaseOrderRequest? Posted;

        public Fixture(StagingPartsDocumentRow doc, params AutohubPoSourceLine[] lines)
        {
            Docs.Setup(d => d.GetByIdAsync(DocId, It.IsAny<CancellationToken>())).ReturnsAsync(doc);
            Repo.Setup(r => r.ListLinesAsync(DocId, It.IsAny<CancellationToken>())).ReturnsAsync(lines.ToList());
            Repo.Setup(r => r.GetPurchaseOrderAsync(DocId, It.IsAny<CancellationToken>())).ReturnsAsync((AutohubPurchaseOrderRecord?)null);
            Sql.Setup(s => s.GetVendorAsync("VEN006", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new VendorSummary { CardCode = "VEN006", CardName = "Vika", Currency = "TZS" });
            Sql.Setup(s => s.GetItemPurchaseStatusAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((IReadOnlyCollection<string> codes, CancellationToken _) =>
                   codes.Select(c => new ItemPurchaseStatus(c, true, false)).ToList());
            Sql.Setup(s => s.FindDocEntryByAppRefAsync("OPOR", DocId.ToString(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(((int, int)?)null);
            Sql.Setup(s => s.GetWarehouseBranchAsync("003", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new WarehouseBranch { BplId = 1, BranchName = "Main", Active = true });
            // 1 EUR = 3,000 TZS; PL01 = cost × 1.25.
            Forex.Setup(f => f.ConvertToTzsAsync(It.IsAny<decimal>(), "EUR", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((decimal amount, string _, DateTime _, CancellationToken _) => amount * 3000m);
            Pricing.Setup(p => p.CalculateAsync(It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((decimal cost, string _, CancellationToken _) => new PricingResult(cost * 1.25m, cost * 2m, cost * 1.5m, 2m));
            Sap.Setup(s => s.FindPurchaseOrderByNumAtCardAsync(It.IsAny<string>(), It.IsAny<string>()))
               .ReturnsAsync(((int, int)?)null);
            Sap.Setup(s => s.CreatePurchaseOrderAsync(It.IsAny<SapPurchaseOrderRequest>()))
               .Callback((SapPurchaseOrderRequest r) => Posted = r)
               .ReturnsAsync(new SapPurchaseOrderResponse { DocEntry = 900, DocNum = 1300 });
        }

        public AutohubPurchaseOrderService Service() => new(
            Docs.Object, Repo.Object, Sql.Object, Sap.Object, Forex.Object, Pricing.Object, Activity.Object,
            Options.Create(new AutohubInventorySettings()), NullLogger<AutohubPurchaseOrderService>.Instance);
    }

    [Fact]
    public void Classify_LeavesOffSkippedPromotionalAndFree_BlocksLinesNotInSap()
    {
        var (include, excluded, problems) = AutohubPurchaseOrderService.Classify(new[]
        {
            Line(1, "created", "VAG1"),
            Line(2, "matched", "VAG2"),
            Line(3, "skip", null),
            Line(4, "matched", "VAG4", promo: true),
            Line(5, "matched", "VAG5", disc: 100),
            Line(6, "pending", null),
            Line(7, "matched", null),
            Line(8, "created", "VAG8", qty: 0),
        });

        Assert.Equal(new[] { 1, 2 }, include.Select(l => l.LineNumber));
        Assert.Equal(new[] { "skipped", "promotional", "free (100% discount)" }, excluded.Select(e => e.Reason));
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("Line 6 is 'pending'"));
        Assert.Contains(problems, p => p.Contains("Line 7 has no SAP item code"));
        Assert.Contains(problems, p => p.Contains("Line 8 (VAG8) has no quantity"));
    }

    [Theory]
    [InlineData(10.0, null, 10.0)]
    [InlineData(10.0, 0.0, 10.0)]
    [InlineData(10.0, 25.0, 7.5)]
    [InlineData(10.0, 100.0, 10.0)]
    public void EffectiveUnitPrice_AppliesTheDiscountLikeBulkCreate(double price, double? disc, double expected) =>
        Assert.Equal((decimal)expected, AutohubPurchaseOrderService.EffectiveUnitPrice((decimal)price, (decimal?)disc));

    [Fact]
    public async Task Preview_CreatedLineUsesStoredPl01_MatchedLineIsCostedFromThisInvoice()
    {
        var f = new Fixture(Doc(),
            Line(1, "created", "VAG1", qty: 2, pl01: 40000m),
            Line(2, "matched", "VAG2", qty: 3, price: 10m, disc: 20m));

        var p = (await f.Service().PreviewAsync(DocId, "VEN006", CancellationToken.None))!;

        Assert.True(p.Ready, string.Join(" | ", p.Blocking));
        Assert.Equal("TZS", p.Currency);
        Assert.Equal("003", p.Warehouse);
        Assert.Equal(40000m, p.Lines[0].UnitPrice);
        Assert.Equal("created", p.Lines[0].PriceSource);
        // 10 EUR less 20% = 8 EUR × 3,000 = 24,000 TZS × 1.25 = 30,000.
        Assert.Equal(30000m, p.Lines[1].UnitPrice);
        Assert.Equal("calculated", p.Lines[1].PriceSource);
        Assert.Equal(2 * 40000m + 3 * 30000m, p.DocTotal);
    }

    [Fact]
    public async Task Preview_UsesTheRememberedVendor()
    {
        var f = new Fixture(Doc(), Line(1, "created", "VAG1", pl01: 100m));
        f.Repo.Setup(r => r.GetVendorAsync("VIKA GmbH", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AutohubSupplierVendor("VIKA GmbH", "VEN006", "Vika"));

        var p = (await f.Service().PreviewAsync(DocId, null, CancellationToken.None))!;

        Assert.Equal("VEN006", p.Vendor!.CardCode);
        Assert.True(p.Vendor.Remembered);
        Assert.True(p.Ready);
    }

    [Fact]
    public async Task Preview_BlocksWhenNotReviewed_NoVendor_ForeignCurrencyVendor_OrItemNotPurchasable()
    {
        var f = new Fixture(Doc(status: "extracted"), Line(1, "created", "VAG1", pl01: 100m));
        var notReviewed = (await f.Service().PreviewAsync(DocId, null, CancellationToken.None))!;
        Assert.False(notReviewed.Ready);
        Assert.Contains(notReviewed.Blocking, b => b.Contains("complete the review"));
        Assert.Contains(notReviewed.Blocking, b => b.Contains("Choose the SAP vendor"));

        var g = new Fixture(Doc(), Line(1, "created", "VAG1", pl01: 100m));
        g.Sql.Setup(s => s.GetVendorAsync("VEN009", It.IsAny<CancellationToken>()))
             .ReturnsAsync(new VendorSummary { CardCode = "VEN009", Currency = "EUR" });
        g.Sql.Setup(s => s.GetItemPurchaseStatusAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<ItemPurchaseStatus> { new("VAG1", PurchaseItem: false, Frozen: false) });
        var foreign = (await g.Service().PreviewAsync(DocId, "VEN009", CancellationToken.None))!;
        Assert.False(foreign.Ready);
        Assert.Contains(foreign.Blocking, b => b.Contains("currency EUR"));
        Assert.Contains(foreign.Blocking, b => b.Contains("Not purchase items in SAP: VAG1"));
    }

    [Fact]
    public async Task Create_PostsTzsPoToWarehouse003WithBranchAndAppRef_RecordsItAndRemembersTheVendor()
    {
        var f = new Fixture(Doc(),
            Line(1, "created", "VAG1", qty: 2, pl01: 40000m),
            Line(2, "skip", null),
            Line(3, "matched", "VAG3", qty: 1, price: 10m));

        var r = await f.Service().CreateAsync(DocId, "VEN006", "anna", CancellationToken.None);

        Assert.Equal(200, r.Status);
        Assert.False(r.AlreadyExisted);
        Assert.Equal(1300, r.PurchaseOrder!.DocNum);
        var req = f.Posted!;
        Assert.Equal("VEN006", req.CardCode);
        Assert.Equal("TZS", req.Currency);
        Assert.Equal("2026INS0342", req.NumAtCard);
        Assert.Equal(new DateTime(2026, 9, 27), req.TaxDate);
        Assert.Equal(1, req.BplId);
        Assert.Equal(DocId.ToString(), req.AppRef);
        Assert.False(req.UseTaxDateRate);
        Assert.Equal(2, req.Lines.Count);
        Assert.All(req.Lines, l => { Assert.Equal("003", l.WarehouseCode); Assert.Equal(0, l.DiscountPercent); });
        Assert.Equal(new[] { 40000d, 37500d }, req.Lines.Select(l => l.UnitPrice));
        f.Repo.Verify(x => x.SavePurchaseOrderAsync(It.Is<AutohubPurchaseOrderRecord>(p =>
            p.DocEntry == 900 && p.LineCount == 2 && p.DocTotal == 117500m && p.CreatedBy == "anna"), It.IsAny<CancellationToken>()), Times.Once);
        f.Repo.Verify(x => x.SaveVendorAsync("VIKA GmbH", "VEN006", "Vika", "anna", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_AlreadyRecorded_ReturnsItWithoutTouchingSap()
    {
        var f = new Fixture(Doc(), Line(1, "created", "VAG1", pl01: 100m));
        var existing = new AutohubPurchaseOrderRecord(DocId, "VEN006", 900, 1300, 1, 100m, "anna", DateTime.UtcNow);
        f.Repo.Setup(r => r.GetPurchaseOrderAsync(DocId, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var r = await f.Service().CreateAsync(DocId, "VEN006", "bob", CancellationToken.None);

        Assert.Equal(200, r.Status);
        Assert.True(r.AlreadyExisted);
        Assert.Same(existing, r.PurchaseOrder);
        f.Sap.Verify(s => s.CreatePurchaseOrderAsync(It.IsAny<SapPurchaseOrderRequest>()), Times.Never);
    }

    [Fact]
    public async Task Create_FoundInSapByAppRef_RecordsItWithoutPostingAgain()
    {
        var f = new Fixture(Doc(), Line(1, "created", "VAG1", pl01: 100m));
        f.Sql.Setup(s => s.FindDocEntryByAppRefAsync("OPOR", DocId.ToString(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((901, 1301));

        var r = await f.Service().CreateAsync(DocId, "VEN006", "bob", CancellationToken.None);

        Assert.True(r.AlreadyExisted);
        Assert.Equal(1301, r.PurchaseOrder!.DocNum);
        f.Repo.Verify(x => x.SavePurchaseOrderAsync(It.Is<AutohubPurchaseOrderRecord>(p => p.DocEntry == 901), It.IsAny<CancellationToken>()), Times.Once);
        f.Sap.Verify(s => s.CreatePurchaseOrderAsync(It.IsAny<SapPurchaseOrderRequest>()), Times.Never);
    }

    [Fact]
    public async Task Create_InvoiceNumberAlreadyOnAPoOfTheVendor_Returns409()
    {
        var f = new Fixture(Doc(), Line(1, "created", "VAG1", pl01: 100m));
        f.Sap.Setup(s => s.FindPurchaseOrderByNumAtCardAsync("VEN006", "2026INS0342")).ReturnsAsync((77, 1277));

        var r = await f.Service().CreateAsync(DocId, "VEN006", "bob", CancellationToken.None);

        Assert.Equal(409, r.Status);
        Assert.Contains("purchase order 1277", Assert.Single(r.Errors));
        f.Sap.Verify(s => s.CreatePurchaseOrderAsync(It.IsAny<SapPurchaseOrderRequest>()), Times.Never);
    }

    [Fact]
    public async Task Create_WarehouseWithoutActiveBranch_Returns422()
    {
        var f = new Fixture(Doc(), Line(1, "created", "VAG1", pl01: 100m));
        f.Sql.Setup(s => s.GetWarehouseBranchAsync("003", It.IsAny<CancellationToken>()))
             .ReturnsAsync(new WarehouseBranch { BplId = 1, BranchName = "Main", Active = false });

        var r = await f.Service().CreateAsync(DocId, "VEN006", "bob", CancellationToken.None);

        Assert.Equal(422, r.Status);
        f.Sap.Verify(s => s.CreatePurchaseOrderAsync(It.IsAny<SapPurchaseOrderRequest>()), Times.Never);
    }

    [Fact]
    public async Task Create_Blocked_Returns400WithTheReasons()
    {
        var f = new Fixture(Doc(), Line(1, "pending", null));

        var r = await f.Service().CreateAsync(DocId, "VEN006", "bob", CancellationToken.None);

        Assert.Equal(400, r.Status);
        Assert.Contains(r.Errors, e => e.Contains("Line 1 is 'pending'"));
        f.Sap.Verify(s => s.CreatePurchaseOrderAsync(It.IsAny<SapPurchaseOrderRequest>()), Times.Never);
    }

    [Fact]
    public void Comments_NameTheInvoiceDateAndSupplier() =>
        Assert.Equal("Autohub invoice 2026INS0342 of 27.09.2026 · VIKA GmbH", AutohubPurchaseOrderService.BuildComments(Doc()));
}
