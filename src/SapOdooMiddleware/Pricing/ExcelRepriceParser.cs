using ClosedXML.Excel;

namespace SapOdooMiddleware.Pricing;

/// <summary>
/// One parsed row from a bulk-reprice Excel upload. A row prices from EITHER an
/// EUR cost, OR a target incl-VAT price on one list (solved to an implied EUR),
/// OR neither (the stored/staging EUR cost is used). Benchmark is optional.
/// </summary>
public record ExcelRepriceRow(
    int RowNumber,
    string ItemCode,
    decimal? EurCost,
    int? TargetPl,
    decimal? TargetInclVat,
    string? CompareItem);

/// <summary>
/// Parses the bulk-reprice workbook: first worksheet. Recognized header names
/// (case-insensitive, first row):
///   ItemCode/Article · EurCost/EUR/Cost/Price · TargetList/TargetPl ·
///   TargetPrice/Target · Benchmark/Compare.
/// Without a header row, columns A=ItemCode, B=EurCost (legacy shape).
/// TargetList accepts 1-4 or names (retail, dealer, superdealer/sd, maasai).
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

        int itemCol = 1, costCol = 2;
        int? targetListCol = null, targetPriceCol = null, benchCol = null;
        int firstDataRow = used.FirstRow().RowNumber();

        // Header detection on the first used row.
        var headerRow = used.FirstRow();
        int? hItem = null, hCost = null;
        foreach (var cell in headerRow.Cells())
        {
            var h = cell.GetString().Trim().ToLowerInvariant().Replace(" ", "");
            if (h.Length == 0) continue;
            int col = cell.Address.ColumnNumber;
            if (hItem is null && (h.Contains("item") || h.Contains("article") || h.Contains("code")))
                hItem = col;
            else if (h.Contains("target") && (h.Contains("list") || h.Contains("pl") || h.Contains("tier")))
                targetListCol = col;
            else if (h.Contains("target"))
                targetPriceCol = col;
            else if (h.Contains("bench") || h.Contains("compare"))
                benchCol = col;
            else if (hCost is null && (h.Contains("eur") || h.Contains("cost") || h.Contains("price")))
                hCost = col;
        }
        if (hItem is not null)
        {
            itemCol = hItem.Value;
            costCol = hCost ?? -1;   // cost column may be absent in a target-only sheet
            firstDataRow = headerRow.RowNumber() + 1;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int r = firstDataRow; r <= used.LastRow().RowNumber(); r++)
        {
            var itemCode = ws.Cell(r, itemCol).GetString().Trim();
            if (itemCode.Length == 0)
                continue;   // blank row

            decimal? cost = null;
            if (costCol > 0 && ws.Cell(r, costCol).TryGetValue<decimal>(out var c) && c > 0m)
                cost = c;

            int? targetPl = null;
            decimal? targetPrice = null;
            if (targetPriceCol is int tpc && ws.Cell(r, tpc).TryGetValue<decimal>(out var tp) && tp > 0m)
                targetPrice = tp;
            if (targetListCol is int tlc)
            {
                var raw = ws.Cell(r, tlc).GetString().Trim().ToLowerInvariant().Replace(" ", "");
                targetPl = raw switch
                {
                    "1" or "pl1" or "retail" => 1,
                    "2" or "pl2" or "dealer" => 2,
                    "3" or "pl3" or "superdealer" or "sd" or "super" => 3,
                    "4" or "pl4" or "maasai" => 4,
                    "" => null,
                    _ => -1,
                };
                if (targetPl == -1)
                {
                    errors.Add($"Row {r} ({itemCode}): unknown target list '{ws.Cell(r, tlc).GetString().Trim()}' " +
                               "(use 1-4 or retail/dealer/superdealer/maasai).");
                    continue;
                }
            }
            if (targetPrice is not null && targetPl is null)
                targetPl = 1;   // target price without a list defaults to Retail
            if (targetPl is not null && targetPrice is null)
            {
                errors.Add($"Row {r} ({itemCode}): target list given but no target price.");
                continue;
            }
            if (cost is not null && targetPrice is not null)
            {
                errors.Add($"Row {r} ({itemCode}): has BOTH an EUR cost and a target price — use one.");
                continue;
            }

            string? bench = null;
            if (benchCol is int bc)
            {
                var b = ws.Cell(r, bc).GetString().Trim();
                if (b.Length > 0) bench = b;
            }

            if (!seen.Add(itemCode))
            {
                errors.Add($"Row {r} ({itemCode}): duplicate item code — first occurrence wins.");
                continue;
            }

            rows.Add(new ExcelRepriceRow(r, itemCode, cost, targetPl, targetPrice, bench));
        }

        return (rows, errors);
    }
}
