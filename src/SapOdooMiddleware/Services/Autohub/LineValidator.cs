using System.Text.RegularExpressions;
using SapOdooMiddleware.Services.Autohub.Excel;
using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Default <see cref="ILineValidator"/>. Rules run in a fixed order so one rule's output feeds the
/// next (e.g. quantity is recovered from arithmetic BEFORE the &gt;200 sanity null, and the arithmetic
/// mismatch warning is suppressed when a recovery already happened). Every mutation is recorded as an
/// issue; legitimate values (e.g. the Germax SKU <c>GL0010</c>) pass through untouched.
/// </summary>
public sealed class LineValidator : ILineValidator
{
    // An OEM-style token: 1-4 leading letters then 4+ digits (e.g. LR090538, C2S52757), optionally
    // followed by more part-number characters. Used to spot an OEM number sitting in the SKU column.
    private static readonly Regex OemLike = new(
        @"^[A-Z]{1,4}\d{4,}[A-Z0-9\-\s]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A value is treated as a clean integer when it sits within this distance of the nearest whole number.
    private const decimal IntegerTolerance = 0.01m;

    public LineValidationResult Validate(PartsInvoiceLine line)
    {
        var issues = new List<LineValidationIssue>();

        var sku  = string.IsNullOrWhiteSpace(line.SupplierArticleNumber) ? null : line.SupplierArticleNumber!.Trim();
        var oems = line.OemNumbers is { Count: > 0 } ? new List<string>(line.OemNumbers) : new List<string>();

        // ---- 1. SKU validation -------------------------------------------------------------------
        if (sku is not null)
        {
            if (sku.Length < 5)
            {
                issues.Add(new("SupplierArticleNumber", "sku_too_short"));
                sku = null;
            }
            else if (sku.All(char.IsDigit))
            {
                // Pure digits in the SKU column is almost always the invoice 'No.' column bleeding across.
                issues.Add(new("SupplierArticleNumber", "sku_pure_digits_row_number_bleed"));
                sku = null;
            }
            else if (OemLike.IsMatch(sku) && !StartsWithKnownPrefix(sku))
            {
                // Looks like an OEM number (not a Germax/Tantivy/VIKA SKU) sitting in the SKU column.
                if (oems.Count == 0)
                {
                    oems.Add(sku);
                    issues.Add(new("SupplierArticleNumber", "sku_oem_swap_migrated"));
                    sku = null;
                }
                else
                {
                    // OEMs already present — don't destroy the SKU, just flag the ambiguity for review.
                    issues.Add(new("SupplierArticleNumber", "sku_ambiguous_both_populated"));
                }
            }
        }

        // ---- 2. Quantity arithmetic recovery -----------------------------------------------------
        var qty = line.Quantity;
        var recovered = false;
        if (qty is > 50m
            && line.UnitPriceForeign is { } up && up > 0m
            && line.LineTotalForeign is { } lt && lt > 0m)
        {
            var net = up * (1m - DiscountFraction(line));
            if (net > 0m)
            {
                var candidate = lt / net;
                var rounded = Math.Round(candidate, MidpointRounding.AwayFromZero);
                if (rounded is >= 1m and <= 50m && Math.Abs(candidate - rounded) <= IntegerTolerance)
                {
                    qty = rounded;
                    recovered = true;
                    issues.Add(new("Quantity", "qty_recovered_from_arithmetic"));
                }
            }
        }

        // ---- 3. Quantity sanity ------------------------------------------------------------------
        if (qty is < 0m)
        {
            qty = 0m;
            issues.Add(new("Quantity", "qty_negative"));
        }
        if (qty is > 200m)
        {
            issues.Add(new("Quantity", "qty_unrealistic_nulled"));
            qty = null;
        }

        // ---- 4. Arithmetic mismatch (only when no recovery happened) -----------------------------
        if (!recovered
            && qty is { } q
            && line.UnitPriceForeign is { } up2
            && line.LineTotalForeign is { } lt2 && lt2 != 0m)
        {
            var expected = up2 * q * (1m - DiscountFraction(line));
            if (Math.Abs(expected - lt2) / Math.Abs(lt2) > 0.05m)
                issues.Add(new("LineTotalForeign", "arithmetic_mismatch"));
        }

        var sanitised = line with
        {
            SupplierArticleNumber = sku,
            OemNumbers = oems,
            Quantity = qty,
        };
        return new LineValidationResult(sanitised, issues);
    }

    private static decimal DiscountFraction(PartsInvoiceLine line)
    {
        var pct = line.DiscountPct ?? 0m;
        if (pct < 0m) pct = 0m;
        if (pct > 100m) pct = 100m;
        return pct / 100m;
    }

    private static bool StartsWithKnownPrefix(string sku) =>
        ExcelTemplateSchema.KnownSkuPrefixes.Any(p => sku.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}
