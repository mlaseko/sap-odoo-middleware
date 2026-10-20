using System.Text.Json.Nodes;
using SapOdooMiddleware.Models.PurchaseSync;
using SapOdooMiddleware.Services.PurchaseSync;

namespace SapOdooMiddleware.Tests.PurchaseSync;

public class ApDocumentMapperTests
{
    private static IccMappings Mappings(DateOnly? cutover = null) => new()
    {
        Settings = new IccApSyncSettings(1, 0.01m, cutover, 7, 10m),
        TaxByVatGroup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["I1"] = 31, ["X0"] = 32 },
        AccountByItemGroup = new Dictionary<int, (int, int?)> { [105] = (501, 9), [107] = (502, null) },
        AccountByGlCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["6011510"] = 601, ["5020100"] = 620 },
    };

    private static SapApDocument EurInvoice(IReadOnlyList<SapApLine>? lines = null, IReadOnlyList<SapApExpense>? expenses = null, string docType = "I") =>
        new(
            ObjType: 18, DocEntry: 1001, DocNum: 2001, CardCode: "S00001", CardName: "LIQUI MOLY GMBH",
            NumAtCard: "LM 12345", DocCur: "EUR", DocRate: 2950m, DocTotal: 2950m * 1180m, DocTotalFc: 1180m,
            DocDate: new DateTime(2026, 11, 3), DocDueDate: new DateTime(2026, 12, 3), TaxDate: null,
            Canceled: "N", DocType: docType, Comments: "test",
            Lines: lines ??
            [
                new SapApLine(0, "23139", "Top Tec 4100 5L", 10m, 100m, 295000m, 1000m, "I1", "5020100", 105, 22, 77, 0),
            ],
            Expenses: expenses ?? [new SapApExpense(1, "Freight", 29500m, 100m, "X0", "6011510")],
            PoDocEntries: [77]);

    [Fact]
    public void ResolveLines_MapsItemGroupAccountTaxAndExpense()
    {
        var lines = ApDocumentMapper.ResolveLines(EurInvoice(), Mappings(), "TZS");

        Assert.Equal(2, lines.Count);
        Assert.Equal("23139 – Top Tec 4100 5L", lines[0].Name);
        Assert.Equal(10m, lines[0].Quantity);
        Assert.Equal(100m, lines[0].PriceUnit);
        Assert.Equal(501, lines[0].AccountId);
        Assert.Equal(31, lines[0].TaxId);
        Assert.Equal(9, lines[0].AnalyticAccountId);

        Assert.Equal("Freight", lines[1].Name);
        Assert.Equal(1m, lines[1].Quantity);
        Assert.Equal(100m, lines[1].PriceUnit);   // foreign document → TotalFrgn
        Assert.Equal(601, lines[1].AccountId);
        Assert.Equal(32, lines[1].TaxId);
    }

    [Fact]
    public void ResolveLines_ServiceTypeUsesGlAccountMap()
    {
        var doc = EurInvoice(
            lines: [new SapApLine(0, null, "Rebate Q3", 1m, 500m, 1475000m, 500m, null, "6011510", null, null, null, null)],
            expenses: [],
            docType: "S");

        var lines = ApDocumentMapper.ResolveLines(doc, Mappings(), "TZS");

        Assert.Single(lines);
        Assert.Equal(601, lines[0].AccountId);
        Assert.Null(lines[0].TaxId);
        Assert.Equal("Rebate Q3", lines[0].Name);
    }

    [Fact]
    public void ResolveLines_MissingItemGroupMapping_ThrowsTerminalAccountNotMapped()
    {
        var doc = EurInvoice(
            lines: [new SapApLine(0, "999", "Unknown group", 1m, 10m, 29500m, 10m, "I1", "5020100", 999, null, null, null)],
            expenses: []);

        var ex = Assert.Throws<PurchaseSyncException>(() => ApDocumentMapper.ResolveLines(doc, Mappings(), "TZS"));
        Assert.Equal(ApDocumentMapper.ErrAccountNotMapped, ex.Code);
        Assert.False(ex.Retryable);
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void ResolveLines_MissingVatGroupMapping_ThrowsTaxNotMapped()
    {
        var doc = EurInvoice(
            lines: [new SapApLine(0, "23139", "x", 1m, 10m, 29500m, 10m, "ZZ", "5020100", 105, null, null, null)],
            expenses: []);

        var ex = Assert.Throws<PurchaseSyncException>(() => ApDocumentMapper.ResolveLines(doc, Mappings(), "TZS"));
        Assert.Equal(ApDocumentMapper.ErrTaxNotMapped, ex.Code);
        Assert.Contains("ZZ", ex.Message);
    }

    [Fact]
    public void ResolveLines_ZeroPriceFallsBackToLineTotalPerUnit()
    {
        var doc = EurInvoice(
            lines: [new SapApLine(0, "23139", "x", 4m, 0m, 118000m, 40m, "I1", null, 105, null, null, null)],
            expenses: []);

        var lines = ApDocumentMapper.ResolveLines(doc, Mappings(), "TZS");
        Assert.Equal(10m, lines[0].PriceUnit);
    }

    [Fact]
    public void BuildMoveValues_Invoice_HasExpectedShape()
    {
        var doc = EurInvoice();
        var lines = ApDocumentMapper.ResolveLines(doc, Mappings(), "TZS");
        var ctx = new ApMoveContext(PartnerId: 12, JournalId: 7, CurrencyId: 3, CompanyCurrency: "TZS",
            InvoiceRateIsInverseOfDocRate: true, ReversedEntryId: null);

        var values = ApDocumentMapper.BuildMoveValues(doc, ctx, lines);

        Assert.Equal("in_invoice", values["move_type"]!.GetValue<string>());
        Assert.Equal(12, values["partner_id"]!.GetValue<int>());
        Assert.Equal(7, values["journal_id"]!.GetValue<int>());
        Assert.Equal(3, values["currency_id"]!.GetValue<int>());
        Assert.Equal("LM 12345", values["ref"]!.GetValue<string>());
        Assert.Equal("2026-11-03", values["invoice_date"]!.GetValue<string>());
        Assert.Equal("2026-12-03", values["invoice_date_due"]!.GetValue<string>());
        Assert.Equal(1001, values[ApDocumentMapper.InvoiceDocEntryField]!.GetValue<int>());
        Assert.Equal("2001", values["x_sap_docnum"]!.GetValue<string>());
        Assert.Equal("18", values["x_sap_objtype"]!.GetValue<string>());
        Assert.Null(values["x_sap_credit_docentry"]);
        Assert.Null(values["reversed_entry_id"]);
        Assert.Equal(1.0 / 2950.0, values["invoice_currency_rate"]!.GetValue<double>(), 12);

        var lineCommands = values["invoice_line_ids"]!.AsArray();
        Assert.Equal(2, lineCommands.Count);
        var first = lineCommands[0]!.AsArray();
        Assert.Equal(0, first[0]!.GetValue<int>());
        var firstVals = first[2]!.AsObject();
        Assert.Equal(501, firstVals["account_id"]!.GetValue<int>());
        Assert.Equal(10.0, firstVals["quantity"]!.GetValue<double>());
        Assert.Equal(100.0, firstVals["price_unit"]!.GetValue<double>());
        // tax_ids = [[6, 0, [31]]]
        var taxCmd = firstVals["tax_ids"]!.AsArray()[0]!.AsArray();
        Assert.Equal(6, taxCmd[0]!.GetValue<int>());
        Assert.Equal(31, taxCmd[2]!.AsArray()[0]!.GetValue<int>());
        Assert.Equal(100, firstVals["analytic_distribution"]!["9"]!.GetValue<int>());
    }

    [Fact]
    public void BuildMoveValues_CreditMemo_SetsCreditFieldsAndReversedEntry()
    {
        var doc = EurInvoice() with { ObjType = 19, DocEntry = 555 };
        var lines = ApDocumentMapper.ResolveLines(doc, Mappings(), "TZS");
        var ctx = new ApMoveContext(12, 7, 3, "TZS", InvoiceRateIsInverseOfDocRate: false, ReversedEntryId: 4242);

        var values = ApDocumentMapper.BuildMoveValues(doc, ctx, lines);

        Assert.Equal("in_refund", values["move_type"]!.GetValue<string>());
        Assert.Equal(555, values[ApDocumentMapper.CreditDocEntryField]!.GetValue<int>());
        Assert.Null(values[ApDocumentMapper.InvoiceDocEntryField]);
        Assert.Equal("19", values["x_sap_objtype"]!.GetValue<string>());
        Assert.Equal(4242, values["reversed_entry_id"]!.GetValue<int>());
        Assert.Equal(2950.0, values["invoice_currency_rate"]!.GetValue<double>(), 6);
    }

    [Fact]
    public void BuildMoveValues_LocalCurrency_OmitsRate()
    {
        var doc = EurInvoice() with { DocCur = "TZS", DocTotalFc = 0m };
        var lines = ApDocumentMapper.ResolveLines(doc, Mappings(), "TZS");
        var values = ApDocumentMapper.BuildMoveValues(doc, new ApMoveContext(1, 7, 2, "TZS", true, null), lines);

        Assert.Null(values["invoice_currency_rate"]);
        Assert.Equal(29500m, lines[1].PriceUnit);   // expense uses LineTotal (LC)
    }

    [Theory]
    [InlineData("TZS", 1180.00, true)]      // foreign doc: compare with DocTotalFC
    [InlineData("TZS", 1180.009, true)]     // inside tolerance
    [InlineData("TZS", 1180.02, false)]     // outside tolerance
    [InlineData("EUR", 1180.00, false)]     // company currency EUR → compare with DocTotal (LC) instead
    public void TotalsMatch_UsesForeignOrLocalTotal(string companyCurrency, double odooTotal, bool expected)
    {
        var doc = EurInvoice();
        Assert.Equal(expected, ApDocumentMapper.TotalsMatch(doc, companyCurrency, (decimal)odooTotal, 0.01m));
    }

    [Fact]
    public void Document_BaseInvoiceDocEntry_OnlyWhenAllLinesShareOneBase()
    {
        var single = EurInvoice(lines:
        [
            new SapApLine(0, "a", "a", 1, 1, 1, 1, null, null, 105, 18, 900, 0),
            new SapApLine(1, "b", "b", 1, 1, 1, 1, null, null, 105, 18, 900, 1),
        ], expenses: []);
        Assert.Equal(900, single.BaseInvoiceDocEntry);

        var mixed = EurInvoice(lines:
        [
            new SapApLine(0, "a", "a", 1, 1, 1, 1, null, null, 105, 18, 900, 0),
            new SapApLine(1, "b", "b", 1, 1, 1, 1, null, null, 105, 18, 901, 0),
        ], expenses: []);
        Assert.Null(mixed.BaseInvoiceDocEntry);
    }

    [Fact]
    public void OdooValues_ReadOdooShapes()
    {
        Assert.Equal(5, OdooValues.Many2OneId(JsonNode.Parse("[5, \"Journal\"]")));
        Assert.Null(OdooValues.Many2OneId(JsonNode.Parse("false")));
        Assert.Null(OdooValues.String(JsonNode.Parse("false")));
        Assert.Equal("I1", OdooValues.String(JsonNode.Parse("\"I1\"")));
        Assert.Equal(new DateOnly(2026, 11, 1), OdooValues.Date(JsonNode.Parse("\"2026-11-01\"")));
        Assert.Null(OdooValues.Date(JsonNode.Parse("false")));
        Assert.Equal(0.01m, OdooValues.Decimal(JsonNode.Parse("0.01"), 9m));
        Assert.Equal(105, OdooValues.Int(JsonNode.Parse("105")));
    }
}
