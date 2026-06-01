using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Ingestion;

public class InvoiceTotalsValidator
{
    private readonly VisionExtractorSettings _settings;
    public InvoiceTotalsValidator(IOptions<VisionExtractorSettings> s) => _settings = s.Value;

    /// <summary>
    /// Validates that Σ(line_total) + freight ≈ total_net within tolerance.
    /// Returns ("ok"|"totals_mismatch"|"no_lines"|"no_footer", notes).
    /// </summary>
    public (string Status, string? Notes) Validate(IEnumerable<InvoiceLine> lines, InvoiceFooter? footer)
    {
        var lineList = lines.ToList();
        if (lineList.Count == 0) return ("no_lines", "No lines extracted.");
        if (footer is null)      return ("no_footer", "No footer/totals visible.");

        var sumLines = lineList.Sum(l => l.LineTotal ?? 0m);
        var freight  = footer.Freight ?? 0m;
        var totalNet = footer.TotalNet ?? footer.InvoiceTotal ?? 0m;

        var diff = Math.Abs((sumLines + freight) - totalNet);
        if (diff <= _settings.TotalsToleranceEur) return ("ok", null);

        return ("totals_mismatch",
            $"Σ(lines)={sumLines:N2} + freight={freight:N2} = {sumLines + freight:N2}, " +
            $"but total_net={totalNet:N2}. Δ={diff:N2} EUR.");
    }
}
