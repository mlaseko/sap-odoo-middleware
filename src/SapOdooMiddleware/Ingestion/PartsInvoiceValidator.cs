using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// Validation for spare-parts invoices. Like the Lubes validator this is advisory (drives the
/// yellow/red banner, never fails the document). Checks Σ(line_total_foreign) ≈ total_amount and
/// flags missing currency / supplier / invoice number.
/// </summary>
public class PartsInvoiceValidator
{
    private readonly VisionExtractorSettings _settings;
    public PartsInvoiceValidator(IOptions<VisionExtractorSettings> s) => _settings = s.Value;

    /// <summary>
    /// Returns (status, notes). Status precedence:
    /// no_lines &gt; totals_mismatch &gt; missing_currency &gt; missing_supplier &gt; missing_invoice_number &gt; ok.
    /// Notes lists every issue found so the operator sees the full picture.
    /// </summary>
    public (string Status, string? Notes) Validate(
        IReadOnlyList<PartsInvoiceLine> lines, PartsInvoiceHeader? header, decimal? totalAmount)
    {
        if (lines.Count == 0) return ("no_lines", "No line items extracted.");

        var issues = new List<string>();
        string? primary = null;

        // Totals reconciliation (only when a grand total was extracted).
        if (totalAmount is { } total)
        {
            var sumLines = lines.Sum(l => l.LineTotalForeign ?? 0m);
            var diff = Math.Abs(sumLines - total);
            if (diff > _settings.TotalsToleranceEur)
            {
                primary ??= "totals_mismatch";
                issues.Add($"Σ(line totals)={sumLines:N2} vs invoice total={total:N2} (Δ={diff:N2}).");
            }
        }

        if (string.IsNullOrWhiteSpace(header?.Currency))
        {
            primary ??= "missing_currency";
            issues.Add("Currency could not be determined.");
        }
        if (string.IsNullOrWhiteSpace(header?.SupplierName))
        {
            primary ??= "missing_supplier";
            issues.Add("Supplier name is missing.");
        }
        if (string.IsNullOrWhiteSpace(header?.InvoiceNumber))
        {
            primary ??= "missing_invoice_number";
            issues.Add("Invoice number is missing.");
        }

        return primary is null ? ("ok", null) : (primary, string.Join(" ", issues));
    }
}
