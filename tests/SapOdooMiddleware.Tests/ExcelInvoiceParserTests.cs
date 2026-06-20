using ClosedXML.Excel;
using SapOdooMiddleware.Services.Autohub.Excel;
using SapOdooMiddleware.Services.Autohub.Excel.Models;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Parsing + hard-failure validation for the Autohub Excel template. Builds workbooks in memory with
/// ClosedXML, mutates them, and asserts the parser behaviour against the acceptance criteria
/// (renamed headers, missing metadata, non-numeric cells, multi-OEM split, blank rows, formulas, dates).
/// </summary>
public class ExcelInvoiceParserTests
{
    private static readonly ExcelInvoiceParser Parser = new();

    private static MemoryStream Valid(Action<IXLWorksheet>? mutate = null)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(ExcelTemplateSchema.SheetName);

        ws.Cell(ExcelTemplateSchema.MetaSupplierRow, ExcelTemplateSchema.MetaValueCol).Value = "Germax";
        ws.Cell(ExcelTemplateSchema.MetaInvoiceNoRow, ExcelTemplateSchema.MetaValueCol).Value = "GAPC260206-T021";
        ws.Cell(ExcelTemplateSchema.MetaInvoiceDateRow, ExcelTemplateSchema.MetaValueCol).Value = "2026-02-06";
        ws.Cell(ExcelTemplateSchema.MetaCurrencyRow, ExcelTemplateSchema.MetaValueCol).Value = "USD";
        ws.Cell(ExcelTemplateSchema.MetaTotalRow, ExcelTemplateSchema.MetaValueCol).Value = 270;

        for (int i = 0; i < ExcelTemplateSchema.ColumnHeaders.Length; i++)
            ws.Cell(ExcelTemplateSchema.HeaderRow, i + 1).Value = ExcelTemplateSchema.ColumnHeaders[i];

        FillRow(ws, ExcelTemplateSchema.FirstDataRow);
        mutate?.Invoke(ws);

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    private static void FillRow(IXLWorksheet ws, int r)
    {
        ws.Cell(r, ExcelTemplateSchema.ColLineNumber).Value = 1;
        ws.Cell(r, ExcelTemplateSchema.ColSku).Value = "GL0010";
        ws.Cell(r, ExcelTemplateSchema.ColOem).Value = "LR014997";
        ws.Cell(r, ExcelTemplateSchema.ColDescription).Value = "Fuel Pump";
        ws.Cell(r, ExcelTemplateSchema.ColBrand).Value = "Germax";
        ws.Cell(r, ExcelTemplateSchema.ColQuantity).Value = 6;
        ws.Cell(r, ExcelTemplateSchema.ColUnit).Value = "Piece";
        ws.Cell(r, ExcelTemplateSchema.ColUnitPrice).Value = 45;
        ws.Cell(r, ExcelTemplateSchema.ColDiscount).Value = 0;
        ws.Cell(r, ExcelTemplateSchema.ColLineTotal).Value = 270;
        ws.Cell(r, ExcelTemplateSchema.ColPromotional).Value = "FALSE";
    }

    [Fact]
    public void ValidWorkbook_Parses()
    {
        using var s = Valid();
        var res = Parser.Parse(s);

        Assert.True(res.Ok);
        var doc = res.Document!;
        Assert.Equal("Germax", doc.Header.SupplierName);
        Assert.Equal("GAPC260206-T021", doc.Header.InvoiceNumber);
        Assert.Equal("USD", doc.Header.Currency);
        Assert.Equal("2026-02-06", doc.Header.InvoiceDate);
        Assert.Equal(270m, doc.Header.TotalAmount);

        var line = Assert.Single(doc.Lines);
        Assert.Equal("GL0010", line.Line.SupplierArticleNumber);
        Assert.Contains("LR014997", line.Line.OemNumbers!);
        Assert.Equal(6m, line.Line.Quantity);
    }

    [Fact]
    public void RenamedHeader_IsHardError()
    {
        using var s = Valid(ws => ws.Cell(ExcelTemplateSchema.HeaderRow, ExcelTemplateSchema.ColSku).Value = "SKU");
        var res = Parser.Parse(s);

        Assert.False(res.Ok);
        Assert.Contains(res.HardErrors, e => e.Field == "SupplierArticleNumber");
    }

    [Fact]
    public void MissingSupplier_IsHardError()
    {
        using var s = Valid(ws => ws.Cell(ExcelTemplateSchema.MetaSupplierRow, ExcelTemplateSchema.MetaValueCol).Clear());
        var res = Parser.Parse(s);

        Assert.False(res.Ok);
        Assert.Contains(res.HardErrors, e => e.Field == "SupplierName");
    }

    [Fact]
    public void InvalidCurrency_IsHardError()
    {
        using var s = Valid(ws => ws.Cell(ExcelTemplateSchema.MetaCurrencyRow, ExcelTemplateSchema.MetaValueCol).Value = "ZZZ");
        var res = Parser.Parse(s);

        Assert.False(res.Ok);
        Assert.Contains(res.HardErrors, e => e.Field == "Currency");
    }

    [Fact]
    public void NonNumericQuantity_IsHardError()
    {
        using var s = Valid(ws => ws.Cell(ExcelTemplateSchema.FirstDataRow, ExcelTemplateSchema.ColQuantity).Value = "eight");
        var res = Parser.Parse(s);

        Assert.False(res.Ok);
        Assert.Contains(res.HardErrors, e => e.Field == "Quantity" && e.Row == ExcelTemplateSchema.FirstDataRow);
    }

    [Fact]
    public void SemicolonSeparatedOems_AreSplit()
    {
        using var s = Valid(ws => ws.Cell(ExcelTemplateSchema.FirstDataRow, ExcelTemplateSchema.ColOem).Value = "LR014997;C2S52757");
        var res = Parser.Parse(s);

        Assert.True(res.Ok);
        var oems = res.Document!.Lines.Single().Line.OemNumbers!;
        Assert.Equal(new[] { "LR014997", "C2S52757" }, oems);
    }

    [Fact]
    public void CommaSeparatedOems_AreSplit_WhenNoSemicolon()
    {
        using var s = Valid(ws => ws.Cell(ExcelTemplateSchema.FirstDataRow, ExcelTemplateSchema.ColOem).Value = "A12345, B67890");
        var res = Parser.Parse(s);

        Assert.True(res.Ok);
        Assert.Equal(new[] { "A12345", "B67890" }, res.Document!.Lines.Single().Line.OemNumbers!);
    }

    [Fact]
    public void BlankMiddleRow_IsSkipped_NoGap()
    {
        using var s = Valid(ws =>
        {
            // row 11 left blank, a second real line on row 12.
            FillRow(ws, ExcelTemplateSchema.FirstDataRow + 2);
            ws.Cell(ExcelTemplateSchema.FirstDataRow + 2, ExcelTemplateSchema.ColSku).Value = "GL2042";
        });
        var res = Parser.Parse(s);

        Assert.True(res.Ok);
        Assert.Equal(2, res.Document!.Lines.Count);
    }

    [Fact]
    public void FormulaLineTotal_IsResolvedToValue()
    {
        using var s = Valid(ws =>
            ws.Cell(ExcelTemplateSchema.FirstDataRow, ExcelTemplateSchema.ColLineTotal).FormulaA1 =
                $"F{ExcelTemplateSchema.FirstDataRow}*H{ExcelTemplateSchema.FirstDataRow}");
        var res = Parser.Parse(s);

        Assert.True(res.Ok);
        Assert.Equal(270m, res.Document!.Lines.Single().Line.LineTotalForeign);
    }

    [Fact]
    public void TextDate_IsParsed()
    {
        using var s = Valid(ws => ws.Cell(ExcelTemplateSchema.MetaInvoiceDateRow, ExcelTemplateSchema.MetaValueCol).Value = "6 Feb 2026");
        var res = Parser.Parse(s);

        Assert.True(res.Ok);
        Assert.Equal("2026-02-06", res.Document!.Header.InvoiceDate);
    }

    [Fact]
    public void NoDataRows_IsHardError()
    {
        using var s = Valid(ws => ws.Row(ExcelTemplateSchema.FirstDataRow).Clear());
        var res = Parser.Parse(s);

        Assert.False(res.Ok);
        Assert.Contains(res.HardErrors, e => e.Field == "(rows)");
    }

    [Fact]
    public void GeneratedTemplate_FilledIn_Parses()
    {
        // Generator + parser agreement: fill the three blank metadata cells in the real template and parse.
        var bytes = new ExcelTemplateGenerator().Generate();
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet(ExcelTemplateSchema.SheetName);
        ws.Cell(ExcelTemplateSchema.MetaSupplierRow, ExcelTemplateSchema.MetaValueCol).Value = "Germax";
        ws.Cell(ExcelTemplateSchema.MetaInvoiceNoRow, ExcelTemplateSchema.MetaValueCol).Value = "INV-1";
        ws.Cell(ExcelTemplateSchema.MetaTotalRow, ExcelTemplateSchema.MetaValueCol).Value = 270;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var res = Parser.Parse(ms);
        Assert.True(res.Ok);
        Assert.Single(res.Document!.Lines);
        Assert.Equal("GL0010", res.Document!.Lines[0].Line.SupplierArticleNumber);
    }
}
