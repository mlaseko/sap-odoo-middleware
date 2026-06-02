using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Ingestion;
using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Tests;

public class PartsInvoiceValidatorTests
{
    private static PartsInvoiceValidator Build() =>
        new(Options.Create(new VisionExtractorSettings { TotalsToleranceEur = 1.00m }));

    private static PartsInvoiceHeader CompleteHeader(decimal? total = 100m) => new()
    {
        SupplierName = "Tantivy Automotive",
        InvoiceNumber = "INV-1",
        Currency = "USD",
        TotalAmount = total,
    };

    private static List<PartsInvoiceLine> Lines(params decimal[] lineTotals) =>
        lineTotals.Select(t => new PartsInvoiceLine { LineTotalForeign = t }).ToList();

    [Fact]
    public void NoLines_ReturnsNoLines()
    {
        var (status, _) = Build().Validate(new List<PartsInvoiceLine>(), CompleteHeader(), 100m);
        Assert.Equal("no_lines", status);
    }

    [Fact]
    public void MatchingTotals_AndCompleteHeader_ReturnsOk()
    {
        var (status, notes) = Build().Validate(Lines(60m, 40m), CompleteHeader(100m), 100m);
        Assert.Equal("ok", status);
        Assert.Null(notes);
    }

    [Fact]
    public void TotalsBeyondTolerance_ReturnsMismatch()
    {
        var (status, notes) = Build().Validate(Lines(60m, 30m), CompleteHeader(100m), 100m);
        Assert.Equal("totals_mismatch", status);
        Assert.NotNull(notes);
    }

    [Fact]
    public void MissingCurrency_ReturnsMissingCurrency()
    {
        var header = new PartsInvoiceHeader { SupplierName = "X", InvoiceNumber = "1", Currency = null, TotalAmount = 100m };
        var (status, _) = Build().Validate(Lines(100m), header, 100m);
        Assert.Equal("missing_currency", status);
    }

    [Fact]
    public void NoTotalAmount_SkipsTotalsCheck()
    {
        // total_amount null (e.g. intermediate page only) → totals not flagged; header complete → ok.
        var header = new PartsInvoiceHeader { SupplierName = "X", InvoiceNumber = "1", Currency = "USD", TotalAmount = null };
        var (status, _) = Build().Validate(Lines(10m, 20m), header, null);
        Assert.Equal("ok", status);
    }
}
