using ClosedXML.Excel;

namespace SapOdooMiddleware.Services.Autohub.Excel;

/// <summary>
/// Builds the blank Autohub invoice template (<c>MolasAutohub_Invoice_Template_v1.xlsx</c>) dynamically
/// from <see cref="ExcelTemplateSchema"/> so the downloaded file's headers and dropdowns always match
/// what <see cref="ExcelInvoiceParser"/> expects. One worksheet: a document-metadata block (rows 1-7),
/// the line header (row 9), and one example row (row 10) the operator overwrites or deletes.
/// </summary>
public sealed class ExcelTemplateGenerator
{
    private static readonly XLColor LabelFill  = XLColor.FromArgb(0x1F, 0x29, 0x37); // dark slate
    private static readonly XLColor HeaderFill  = XLColor.FromArgb(0x11, 0x82, 0x7A); // teal
    private static readonly XLColor InputFill   = XLColor.FromArgb(0xF1, 0xF5, 0xF9); // light
    private static readonly XLColor ExampleFill = XLColor.FromArgb(0xFE, 0xF9, 0xC3); // pale yellow

    public byte[] Generate()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(ExcelTemplateSchema.SheetName);

        BuildMetadataBlock(ws);
        BuildLineHeader(ws);
        BuildExampleRow(ws);
        ApplyDropdowns(ws);
        ApplyLayout(ws);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void BuildMetadataBlock(IXLWorksheet ws)
    {
        (int Row, string Label, string Hint, bool Required)[] meta =
        {
            (ExcelTemplateSchema.MetaSupplierRow,    "SupplierName",   "e.g. Germax", true),
            (ExcelTemplateSchema.MetaInvoiceNoRow,   "InvoiceNumber",  "e.g. GAPC260206-T021", true),
            (ExcelTemplateSchema.MetaInvoiceDateRow, "InvoiceDate",    "e.g. 2026-02-06", true),
            (ExcelTemplateSchema.MetaCurrencyRow,    "Currency",       "pick from the list", true),
            (ExcelTemplateSchema.MetaTotalRow,       "TotalAmount",    "invoice grand total", true),
            (ExcelTemplateSchema.MetaForexRateRow,   "ForexRateUsed",  "optional — TZS rate", false),
            (ExcelTemplateSchema.MetaForexDateRow,   "ForexRateDate",  "optional — defaults to invoice date", false),
        };

        foreach (var (row, label, hint, required) in meta)
        {
            var labelCell = ws.Cell(row, ExcelTemplateSchema.MetaLabelCol);
            labelCell.Value = required ? label + " *" : label;
            labelCell.Style.Font.Bold = true;
            labelCell.Style.Font.FontColor = XLColor.White;
            labelCell.Style.Fill.BackgroundColor = LabelFill;

            var valueCell = ws.Cell(row, ExcelTemplateSchema.MetaValueCol);
            valueCell.Style.Fill.BackgroundColor = InputFill;
            valueCell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

            // Hint sits to the right of the value cell, greyed out.
            var hintCell = ws.Cell(row, ExcelTemplateSchema.MetaValueCol + 1);
            hintCell.Value = hint;
            hintCell.Style.Font.Italic = true;
            hintCell.Style.Font.FontColor = XLColor.Gray;
        }

        // Example metadata values so the operator sees the expected formats.
        ws.Cell(ExcelTemplateSchema.MetaCurrencyRow, ExcelTemplateSchema.MetaValueCol).Value = "USD";
        ws.Cell(ExcelTemplateSchema.MetaInvoiceDateRow, ExcelTemplateSchema.MetaValueCol).Value = "2026-02-06";
    }

    private static void BuildLineHeader(IXLWorksheet ws)
    {
        for (int i = 0; i < ExcelTemplateSchema.ColumnHeaders.Length; i++)
        {
            var cell = ws.Cell(ExcelTemplateSchema.HeaderRow, i + 1);
            cell.Value = ExcelTemplateSchema.ColumnHeaders[i];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = HeaderFill;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }
    }

    private static void BuildExampleRow(IXLWorksheet ws)
    {
        int r = ExcelTemplateSchema.FirstDataRow;
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
        ws.Cell(r, ExcelTemplateSchema.ColNotes).Value = "example row — overwrite or delete";

        ws.Range(r, 1, r, ExcelTemplateSchema.LastColumn).Style.Fill.BackgroundColor = ExampleFill;
    }

    private static void ApplyDropdowns(IXLWorksheet ws)
    {
        const int lastValidatedRow = 1000;

        // Currency on the metadata value cell.
        var currency = ws.Cell(ExcelTemplateSchema.MetaCurrencyRow, ExcelTemplateSchema.MetaValueCol)
            .CreateDataValidation();
        currency.List(InlineList(ExcelTemplateSchema.Currencies), true);
        currency.IgnoreBlanks = true;

        // Unit on the whole data column.
        var unit = ws.Range(
                ExcelTemplateSchema.FirstDataRow, ExcelTemplateSchema.ColUnit,
                lastValidatedRow, ExcelTemplateSchema.ColUnit)
            .CreateDataValidation();
        unit.List(InlineList(ExcelTemplateSchema.Units), true);
        unit.IgnoreBlanks = true;

        // IsPromotional TRUE/FALSE on the whole data column.
        var promo = ws.Range(
                ExcelTemplateSchema.FirstDataRow, ExcelTemplateSchema.ColPromotional,
                lastValidatedRow, ExcelTemplateSchema.ColPromotional)
            .CreateDataValidation();
        promo.List(InlineList(new[] { "TRUE", "FALSE" }), true);
        promo.IgnoreBlanks = true;
    }

    private static void ApplyLayout(IXLWorksheet ws)
    {
        ws.Column(ExcelTemplateSchema.ColLineNumber).Width = 11;
        ws.Column(ExcelTemplateSchema.ColSku).Width = 20;
        ws.Column(ExcelTemplateSchema.ColOem).Width = 24;
        ws.Column(ExcelTemplateSchema.ColDescription).Width = 30;
        ws.Column(ExcelTemplateSchema.ColBrand).Width = 14;
        ws.Column(ExcelTemplateSchema.ColQuantity).Width = 10;
        ws.Column(ExcelTemplateSchema.ColUnit).Width = 10;
        ws.Column(ExcelTemplateSchema.ColUnitPrice).Width = 14;
        ws.Column(ExcelTemplateSchema.ColDiscount).Width = 11;
        ws.Column(ExcelTemplateSchema.ColLineTotal).Width = 16;
        ws.Column(ExcelTemplateSchema.ColPromotional).Width = 13;
        ws.Column(ExcelTemplateSchema.ColNotes).Width = 30;

        // Keep the header (and the metadata block above it) visible while scrolling the data rows.
        ws.SheetView.FreezeRows(ExcelTemplateSchema.HeaderRow);
    }

    /// <summary>ClosedXML inline list: the values wrapped in one double-quoted, comma-separated string.</summary>
    private static string InlineList(IEnumerable<string> values) => "\"" + string.Join(",", values) + "\"";
}
