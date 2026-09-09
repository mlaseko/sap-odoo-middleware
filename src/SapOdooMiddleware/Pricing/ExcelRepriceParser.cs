using ClosedXML.Excel;

namespace SapOdooMiddleware.Pricing;

/// <summary>One parsed row from a bulk-reprice Excel upload.</summary>
public record ExcelRepriceRow(int RowNumber, string ItemCode, decimal EurCost);

/// <summary>
/// Parses the bulk-reprice workbook: first worksheet, columns ItemCode + EurCost.
/// A header row is detected by name (any header containing "item"/"code" and
/// "eur"/"cost"/"price"); without one, columns A and B are used as-is.
/// </summary>
public static class ExcelRepriceParser
{
    public static (List<ExcelRepriceRow> Rows, List<string> Errors) Parse(Stream xlsx)
    {
        var rows = new List<ExcelRepriceRow>();
        var errors = new List<string>();

        using var wb = new XLWorkbook(xlsx);
        var ws = wb.Worksheets.Worksheet(1);
        var used = ws.RangeUsed();
        if (used is null)
            return (rows, new List<string> { "The worksheet is empty." });

        int itemCol = 1, costCol = 2, firstDataRow = used.FirstRow().RowNumber();

        // Header detection on the first used row.
        var headerRow = used.FirstRow();
        int? hItem = null, hCost = null;
        foreach (var cell in headerRow.Cells())
        {
            var h = cell.GetString().Trim().ToLowerInvariant();
            if (h.Length == 0) continue;
            if (hItem is null && (h.Contains("item") || h.Contains("article") || h.Contains("code")))
                hItem = cell.Address.ColumnNumber;
            else if (hCost is null && (h.Contains("eur") || h.Contains("cost") || h.Contains("price")))
                hCost = cell.Address.ColumnNumber;
        }
        if (hItem is not null && hCost is not null)
        {
            itemCol = hItem.Value;
            costCol = hCost.Value;
            firstDataRow = headerRow.RowNumber() + 1;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int r = firstDataRow; r <= used.LastRow().RowNumber(); r++)
        {
            var itemCode = ws.Cell(r, itemCol).GetString().Trim();
            var costCell = ws.Cell(r, costCol);
            if (itemCode.Length == 0 && costCell.IsEmpty())
                continue;   // blank row

            if (itemCode.Length == 0)
            {
                errors.Add($"Row {r}: missing item code.");
                continue;
            }
            if (!costCell.TryGetValue<decimal>(out var cost) || cost <= 0m)
            {
                errors.Add($"Row {r} ({itemCode}): EUR cost is not a positive number.");
                continue;
            }
            if (!seen.Add(itemCode))
            {
                errors.Add($"Row {r} ({itemCode}): duplicate item code — first occurrence wins.");
                continue;
            }

            rows.Add(new ExcelRepriceRow(r, itemCode, cost));
        }

        return (rows, errors);
    }
}
