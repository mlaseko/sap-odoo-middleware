using SapOdooMiddleware.Services.Autohub;
using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// The shared per-line validator (used by both the Excel import and the PDF/vision path). Asserts the
/// sanitisation rules and, crucially, that legitimate values are left untouched (no regressions).
/// </summary>
public class LineValidatorTests
{
    private static readonly LineValidator V = new();

    private static PartsInvoiceLine Line(
        string? sku = null, IEnumerable<string>? oems = null,
        decimal? qty = null, decimal? price = null, decimal? total = null, decimal? disc = null)
        => new()
        {
            SupplierArticleNumber = sku,
            OemNumbers = oems?.ToList(),
            Quantity = qty,
            UnitPriceForeign = price,
            LineTotalForeign = total,
            DiscountPct = disc,
        };

    private static bool Has(LineValidationResult r, string code) => r.Issues.Any(i => i.Code == code);

    [Fact]
    public void ShortSku_IsNulled()
    {
        var r = V.Validate(Line(sku: "GL1"));
        Assert.Null(r.Line.SupplierArticleNumber);
        Assert.True(Has(r, "sku_too_short"));
    }

    [Fact]
    public void PureDigitSku_IsNulled_RowNumberBleed()
    {
        var r = V.Validate(Line(sku: "185"));
        Assert.Null(r.Line.SupplierArticleNumber);
        Assert.True(Has(r, "sku_pure_digits_row_number_bleed"));
    }

    [Fact]
    public void OemInSkuColumn_NoOems_IsMigrated()
    {
        var r = V.Validate(Line(sku: "LR090538"));
        Assert.Null(r.Line.SupplierArticleNumber);
        Assert.Contains("LR090538", r.Line.OemNumbers!);
        Assert.True(Has(r, "sku_oem_swap_migrated"));
    }

    [Fact]
    public void OemInSkuColumn_WithExistingOems_IsFlaggedNotNulled()
    {
        var r = V.Validate(Line(sku: "LR090538", oems: new[] { "C2S52757" }));
        Assert.Equal("LR090538", r.Line.SupplierArticleNumber);   // kept — ambiguous, operator decides
        Assert.True(Has(r, "sku_ambiguous_both_populated"));
    }

    [Fact]
    public void GermaxSku_IsPreserved_NoIssues()
    {
        // Acceptance #8: GL0010 must survive untouched (the v3 DGX truncation bug must not be reintroduced).
        var r = V.Validate(Line(sku: "GL0010", qty: 6m, price: 45m, total: 270m));
        Assert.Equal("GL0010", r.Line.SupplierArticleNumber);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Quantity_RecoveredFromArithmetic()
    {
        // qty looks like an invoice row number (162); 270 / 45 = 6 is the real quantity.
        var r = V.Validate(Line(sku: "GL0010", qty: 162m, price: 45m, total: 270m));
        Assert.Equal(6m, r.Line.Quantity);
        Assert.True(Has(r, "qty_recovered_from_arithmetic"));
    }

    [Fact]
    public void Quantity_RecoveredFromArithmetic_WithDiscount()
    {
        // net price = 50 * (1 - 0.10) = 45; 450 / 45 = 10.
        var r = V.Validate(Line(sku: "GL0010", qty: 219m, price: 50m, total: 450m, disc: 10m));
        Assert.Equal(10m, r.Line.Quantity);
        Assert.True(Has(r, "qty_recovered_from_arithmetic"));
    }

    [Fact]
    public void Quantity_Unrealistic_NoRecovery_IsNulled()
    {
        // No price/total to recover from → >200 is nulled for manual review.
        var r = V.Validate(Line(sku: "GL0010", qty: 500m));
        Assert.Null(r.Line.Quantity);
        Assert.True(Has(r, "qty_unrealistic_nulled"));
    }

    [Fact]
    public void Quantity_Negative_IsClampedToZero()
    {
        var r = V.Validate(Line(sku: "GL0010", qty: -3m, price: 10m, total: 0m));
        Assert.Equal(0m, r.Line.Quantity);
        Assert.True(Has(r, "qty_negative"));
    }

    [Fact]
    public void ArithmeticMismatch_IsWarned_NotModified()
    {
        // qty 2, price 10 → expected 20, but line total says 50 → mismatch (>5%). Value untouched.
        var r = V.Validate(Line(sku: "GL0010", qty: 2m, price: 10m, total: 50m));
        Assert.Equal(2m, r.Line.Quantity);
        Assert.Equal(50m, r.Line.LineTotalForeign);
        Assert.True(Has(r, "arithmetic_mismatch"));
    }

    [Fact]
    public void CleanLine_HasNoIssues()
    {
        var r = V.Validate(Line(sku: "GL2042", qty: 6m, price: 45m, total: 270m));
        Assert.Empty(r.Issues);
    }
}
