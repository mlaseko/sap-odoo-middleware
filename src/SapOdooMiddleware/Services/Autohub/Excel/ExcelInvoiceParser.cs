using System.Globalization;
using ClosedXML.Excel;
using SapOdooMiddleware.Services.Autohub.Excel.Models;
using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Services.Autohub.Excel;

/// <summary>
/// Reads a filled-in Autohub invoice workbook (<see cref="ExcelTemplateSchema"/>) into the same DTO
/// shape the vision path produces. Pure parsing + hard-failure validation only — the per-line quality
/// rules (SKU/qty sanitisation) live in <see cref="ILineValidator"/> and run afterwards, so behaviour
/// matches the PDF path. Formula cells are resolved to their computed values by ClosedXML.
/// </summary>
public sealed class ExcelInvoiceParser
{
    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "d MMM yyyy", "dd MMM yyyy",
        "d MMMM yyyy", "dd MMMM yyyy", "MMM d yyyy", "MMMM d yyyy", "yyyy.MM.dd",
    };

    /// <summary>Parse a workbook stream. Never throws for malformed content — returns hard errors instead.</summary>
    public ExcelParseResult Parse(Stream xlsx)
    {
        XLWorkbook wb;
        try
        {
            wb = new XLWorkbook(xlsx);
        }
        catch (Exception ex)
        {
            return ExcelParseResult.Fail(new ExcelParseError(0, "File", $"Not a readable .xlsx workbook: {ex.Message}"));
        }

        using (wb)
        {
            if (!wb.Worksheets.TryGetWorksheet(ExcelTemplateSchema.SheetName, out var ws))
                return ExcelParseResult.Fail(new ExcelParseError(0, "Sheet",
                    $"Worksheet '{ExcelTemplateSchema.SheetName}' not found."));

            var errors = new List<ExcelParseError>();

            // ---- Header row integrity (catches renamed/missing headers). ----
            for (int i = 0; i < ExcelTemplateSchema.ColumnHeaders.Length; i++)
            {
                var expected = ExcelTemplateSchema.ColumnHeaders[i];
                var actual = ws.Cell(ExcelTemplateSchema.HeaderRow, i + 1).GetString().Trim();
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    errors.Add(new ExcelParseError(ExcelTemplateSchema.HeaderRow, expected,
                        $"header column must be '{expected}' but was '{actual}'"));
            }
            // Headers are structural — if they're wrong, row parsing below is meaningless.
            if (errors.Count > 0)
                return ExcelParseResult.Fail(errors);

            // ---- Document metadata (column B of rows 1-7). ----
            var supplierName  = MetaText(ws, ExcelTemplateSchema.MetaSupplierRow);
            var invoiceNumber = MetaText(ws, ExcelTemplateSchema.MetaInvoiceNoRow);
            var currency      = MetaText(ws, ExcelTemplateSchema.MetaCurrencyRow);

            if (supplierName is null)  errors.Add(new(ExcelTemplateSchema.MetaSupplierRow, "SupplierName", "required, was blank"));
            if (invoiceNumber is null) errors.Add(new(ExcelTemplateSchema.MetaInvoiceNoRow, "InvoiceNumber", "required, was blank"));
            if (currency is null)
                errors.Add(new(ExcelTemplateSchema.MetaCurrencyRow, "Currency", "required, was blank"));
            else if (!ExcelTemplateSchema.Currencies.Contains(currency, StringComparer.OrdinalIgnoreCase))
                errors.Add(new(ExcelTemplateSchema.MetaCurrencyRow, "Currency",
                    $"'{currency}' is not one of {string.Join("/", ExcelTemplateSchema.Currencies)}"));

            var totalCell = MetaCell(ws, ExcelTemplateSchema.MetaTotalRow);
            decimal? totalAmount = null;
            if (totalCell.IsEmpty()) errors.Add(new(ExcelTemplateSchema.MetaTotalRow, "TotalAmount", "required, was blank"));
            else if (totalCell.TryGetValue(out decimal t)) totalAmount = t;
            else errors.Add(new(ExcelTemplateSchema.MetaTotalRow, "TotalAmount", $"not a number: '{totalCell.GetString()}'"));

            var dateCell = MetaCell(ws, ExcelTemplateSchema.MetaInvoiceDateRow);
            DateTime? invoiceDate = null;
            if (dateCell.IsEmpty()) errors.Add(new(ExcelTemplateSchema.MetaInvoiceDateRow, "InvoiceDate", "required, was blank"));
            else if (TryParseDate(dateCell, out var d)) invoiceDate = d;
            else errors.Add(new(ExcelTemplateSchema.MetaInvoiceDateRow, "InvoiceDate", $"not a valid date: '{dateCell.GetString()}'"));

            // Optional forex hints (audit only).
            decimal? forexRate = null;
            var forexCell = MetaCell(ws, ExcelTemplateSchema.MetaForexRateRow);
            if (!forexCell.IsEmpty())
            {
                if (forexCell.TryGetValue(out decimal fr)) forexRate = fr;
                else errors.Add(new(ExcelTemplateSchema.MetaForexRateRow, "ForexRateUsed", $"not a number: '{forexCell.GetString()}'"));
            }
            DateTime? forexDate = null;
            var forexDateCell = MetaCell(ws, ExcelTemplateSchema.MetaForexDateRow);
            if (!forexDateCell.IsEmpty())
            {
                if (TryParseDate(forexDateCell, out var fd)) forexDate = fd;
                else errors.Add(new(ExcelTemplateSchema.MetaForexDateRow, "ForexRateDate", $"not a valid date: '{forexDateCell.GetString()}'"));
            }

            // ---- Line rows (row 10 .. last used row). Blank rows are skipped, not gaps. ----
            var lines = new List<ParsedExcelLine>();
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? (ExcelTemplateSchema.FirstDataRow - 1);
            for (int r = ExcelTemplateSchema.FirstDataRow; r <= lastRow; r++)
            {
                if (RowIsBlank(ws, r)) continue;

                var description = CellText(ws, r, ExcelTemplateSchema.ColDescription);
                if (description is null)
                    errors.Add(new(r, "Description", "required, was blank"));

                var qty = RequiredNumber(ws, r, ExcelTemplateSchema.ColQuantity, "Quantity", errors);
                var unitPrice = RequiredNumber(ws, r, ExcelTemplateSchema.ColUnitPrice, "UnitPriceForeign", errors);
                var lineTotal = RequiredNumber(ws, r, ExcelTemplateSchema.ColLineTotal, "LineTotalForeign", errors);

                var unit = CellText(ws, r, ExcelTemplateSchema.ColUnit);
                if (unit is null)
                    errors.Add(new(r, "Unit", "required, was blank"));

                decimal? discount = null;
                var discCell = ws.Cell(r, ExcelTemplateSchema.ColDiscount);
                if (!discCell.IsEmpty())
                {
                    if (discCell.TryGetValue(out decimal dp)) discount = dp;
                    else errors.Add(new(r, "DiscountPct", $"not a number: '{discCell.GetString()}'"));
                }

                lines.Add(new ParsedExcelLine(
                    ExcelRow: r,
                    Line: new PartsInvoiceLine
                    {
                        SupplierArticleNumber = CellText(ws, r, ExcelTemplateSchema.ColSku),
                        OemNumbers            = ParseOems(CellText(ws, r, ExcelTemplateSchema.ColOem)),
                        Description           = description,
                        Brand                 = CellText(ws, r, ExcelTemplateSchema.ColBrand),
                        Quantity              = qty,
                        Unit                  = unit,
                        UnitPriceForeign      = unitPrice,
                        DiscountPct           = discount,
                        LineTotalForeign      = lineTotal,
                    },
                    ExplicitPromotional: ParseBool(ws.Cell(r, ExcelTemplateSchema.ColPromotional))));
            }

            if (lines.Count == 0)
                errors.Add(new(ExcelTemplateSchema.FirstDataRow, "(rows)", "at least one data row is required"));

            if (errors.Count > 0)
                return ExcelParseResult.Fail(errors);

            var header = new PartsInvoiceHeader
            {
                SupplierName  = supplierName,
                InvoiceNumber = invoiceNumber,
                InvoiceDate   = invoiceDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Currency      = currency!.ToUpperInvariant(),
                TotalAmount   = totalAmount,
            };
            return ExcelParseResult.Success(new ParsedExcelDocument(header, forexRate, forexDate, lines));
        }
    }

    // ---- cell helpers ----------------------------------------------------------------------------

    private static IXLCell MetaCell(IXLWorksheet ws, int row) => ws.Cell(row, ExcelTemplateSchema.MetaValueCol);

    private static string? MetaText(IXLWorksheet ws, int row) => NullIfBlank(MetaCell(ws, row).GetString());

    private static string? CellText(IXLWorksheet ws, int row, int col) => NullIfBlank(ws.Cell(row, col).GetString());

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool RowIsBlank(IXLWorksheet ws, int row)
    {
        for (int c = 1; c <= ExcelTemplateSchema.LastColumn; c++)
            if (!ws.Cell(row, c).IsEmpty()) return false;
        return true;
    }

    private static decimal? RequiredNumber(IXLWorksheet ws, int row, int col, string field, List<ExcelParseError> errors)
    {
        var cell = ws.Cell(row, col);
        if (cell.IsEmpty())
        {
            errors.Add(new(row, field, "required, was blank"));
            return null;
        }
        if (cell.TryGetValue(out decimal v)) return v;
        errors.Add(new(row, field, $"not a number: '{cell.GetString()}'"));
        return null;
    }

    private static List<string> ParseOems(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        var separators = raw.Contains(';') ? new[] { ';' } : new[] { ',' };
        return raw.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Where(s => s.Length > 0)
                  .ToList();
    }

    private static bool ParseBool(IXLCell c)
    {
        if (c.IsEmpty()) return false;
        if (c.DataType == XLDataType.Boolean && c.TryGetValue(out bool b)) return b;
        var s = c.GetString().Trim();
        return s.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
            || s.Equals("YES", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Y", StringComparison.OrdinalIgnoreCase)
            || s == "1";
    }

    private static bool TryParseDate(IXLCell c, out DateTime value)
    {
        if (c.DataType == XLDataType.DateTime && c.TryGetValue(out DateTime dt))
        {
            value = dt;
            return true;
        }
        var s = c.GetString().Trim();
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            return true;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
