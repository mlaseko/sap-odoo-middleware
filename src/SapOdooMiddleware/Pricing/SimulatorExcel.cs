using System.Globalization;
using ClosedXML.Excel;

namespace SapOdooMiddleware.Pricing;

/// <summary>
/// Excel in/out for the price simulator. Upload: first worksheet, header row required, columns
/// matched by name (case-insensitive, any order):
///   Reference/Item/Article/Code · Description/Name · Cost/EUR/Price · Currency ·
///   Band/Category · SapGroup/Group · OdooCategory · Benchmark/Compare.
/// Cost is required; Currency defaults to EUR; one of Band, SapGroup or OdooCategory is needed.
/// </summary>
public static class SimulatorExcel
{
    public static (List<SimulationInput> Rows, List<string> Errors) Parse(Stream xlsx)
    {
        var rows = new List<SimulationInput>();
        var errors = new List<string>();
        using var wb = new XLWorkbook(xlsx);
        var ws = wb.Worksheets.Worksheet(1);
        var used = ws.RangeUsed();
        if (used is null) return (rows, new List<string> { "The worksheet is empty." });

        int? cRef = null, cDesc = null, cCost = null, cCur = null, cBand = null, cGroup = null, cOdoo = null, cBench = null;
        foreach (var cell in used.FirstRow().Cells())
        {
            var h = cell.GetString().Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
            var col = cell.Address.ColumnNumber;
            if (h.Length == 0) continue;
            if (h.Contains("odoo")) cOdoo ??= col;
            else if (h.Contains("bench") || h.Contains("compare")) cBench ??= col;
            else if (h.Contains("currency") || h == "cur") cCur ??= col;
            else if (h.Contains("sapgroup") || h == "group" || h.Contains("itemgroup")) cGroup ??= col;
            else if (h.Contains("band") || h.Contains("category")) cBand ??= col;
            else if (h.Contains("desc") || h == "name" || h.Contains("itemname")) cDesc ??= col;
            else if (h.Contains("cost") || h.Contains("eur") || h.Contains("price")) cCost ??= col;
            else if (h.Contains("ref") || h.Contains("item") || h.Contains("article") || h.Contains("code")) cRef ??= col;
        }
        if (cCost is null)
            return (rows, new List<string> { "No Cost column found. Put a header row with at least: Cost, and Band (or SapGroup)." });

        string? Str(IXLRangeRow r, int? c)
        {
            if (c is null) return null;
            var v = r.Cell(c.Value).GetString().Trim();
            return v.Length == 0 ? null : v;
        }

        foreach (var r in used.RowsUsed().Skip(1))
        {
            var n = r.RowNumber();
            var costText = Str(r, cCost);
            if (costText is null && Str(r, cRef) is null && Str(r, cDesc) is null) continue;   // blank row
            if (!TryNumber(r.Cell(cCost.Value), out var cost) || cost <= 0m)
            {
                errors.Add($"Row {n}: cost '{costText}' is not a positive number — skipped.");
                continue;
            }
            int? group = null;
            var groupText = Str(r, cGroup);
            if (groupText is not null)
            {
                if (int.TryParse(groupText.Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g)) group = g;
                else errors.Add($"Row {n}: SAP group '{groupText}' is not a number — ignored.");
            }
            rows.Add(new SimulationInput(
                Str(r, cRef), Str(r, cDesc), cost, Str(r, cCur) ?? "EUR",
                Str(r, cBand), group, Str(r, cOdoo), Str(r, cBench)));
        }
        if (rows.Count == 0 && errors.Count == 0) errors.Add("No data rows found under the header.");
        return (rows, errors);
    }

    private static bool TryNumber(IXLCell cell, out decimal value)
    {
        value = 0m;
        if (cell.DataType == XLDataType.Number) { value = (decimal)cell.GetDouble(); return true; }
        var s = cell.GetString().Replace(",", "").Replace("€", "").Replace("TZS", "", StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Results workbook: one row per item, prices incl/excl VAT, margins, notes.</summary>
    public static byte[] Write(IReadOnlyList<SimulationResult> results)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Simulated prices");
        var headers = new[]
        {
            "Reference", "Description", "Cost", "Currency", "Rate", "Cost TZS", "Band",
            "PL1 Retail incl VAT", "PL2 Dealer incl VAT", "PL3 Super Dealer incl VAT", "PL4 Maasai incl VAT",
            "PL1 net", "PL2 net", "PL3 net", "PL4 net",
            "PL1 margin %", "PL2 margin %", "PL3 margin %", "PL4 margin %",
            "Retail ÷ cost", "Benchmark", "Benchmark PL1 incl VAT", "Error", "Warnings", "Notes", "How the band was chosen",
        };
        for (int i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(1, i + 1);
            c.Value = headers[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E78");
            c.Style.Font.FontColor = XLColor.White;
        }

        int row = 2;
        foreach (var r in results)
        {
            int col = 1;
            void Put(object? v)
            {
                var cell = ws.Cell(row, col++);
                switch (v)
                {
                    case null: break;
                    case decimal d: cell.Value = d; break;
                    case string s: cell.Value = s; break;
                    default: cell.Value = v.ToString(); break;
                }
            }
            Put(r.Input.Reference); Put(r.Input.Description); Put(r.Input.Cost); Put(r.Input.Currency?.ToUpperInvariant());
            Put(r.Rate); Put(r.CostTzs); Put(r.Band);
            for (int pl = 1; pl <= 4; pl++) Put(r.InclVat?.GetValueOrDefault(pl));
            for (int pl = 1; pl <= 4; pl++) Put(r.Net?.GetValueOrDefault(pl));
            for (int pl = 1; pl <= 4; pl++) Put(r.MarginPct?.GetValueOrDefault(pl));
            Put(r.InclVat is not null && r.CostTzs is > 0m ? Math.Round(r.InclVat[1] / r.CostTzs.Value, 2) : null);
            Put(r.Benchmark?.ItemCode);
            Put(r.Benchmark is { Found: true } b && b.OldNet.TryGetValue(1, out var bn) ? Math.Round(bn * 1.18m) : null);
            Put(r.Error);
            Put(string.Join(" | ", r.Warnings));
            Put(string.Join(" | ", r.Notes));
            Put(r.BandPath);
            row++;
        }

        foreach (var c in new[] { 3, 5, 6 }.Concat(Enumerable.Range(8, 8)).Append(22))
            ws.Column(c).Style.NumberFormat.Format = "#,##0.##";
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, 22).AdjustToContents(1, Math.Min(row, 200));

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>A ready-to-fill template for the upload.</summary>
    public static byte[] Template()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Items to price");
        var h = new[] { "Reference", "Description", "Cost", "Currency", "Band", "SapGroup", "OdooCategory", "Benchmark" };
        for (int i = 0; i < h.Length; i++) { ws.Cell(1, i + 1).Value = h[i]; ws.Cell(1, i + 1).Style.Font.Bold = true; }
        ws.Cell(2, 1).Value = "NEW-1"; ws.Cell(2, 2).Value = "Example engine oil 5 L"; ws.Cell(2, 3).Value = 32.5;
        ws.Cell(2, 4).Value = "EUR"; ws.Cell(2, 5).Value = "Engine Oils"; ws.Cell(2, 8).Value = "";
        ws.Cell(3, 1).Value = "NEW-2"; ws.Cell(3, 2).Value = "Example grease 400 g"; ws.Cell(3, 3).Value = 9.8;
        ws.Cell(3, 4).Value = "EUR"; ws.Cell(3, 6).Value = 117;
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
