using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SapOdooMiddleware.Pricing;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

/// <summary>The LIQUI MOLY prices shown on /pricing, read from Odoo's Price History (molas_supplier_orders).</summary>
public class SupplierPriceHistoryReaderTests
{
    private static JsonObject Row(string date, string inv, double qty, double list, double disc, double net, JsonNode? change)
        => new()
        {
            ["invoice_date"] = date, ["invoice_no"] = inv, ["quantity"] = qty, ["unit_price"] = list,
            ["discount"] = disc, ["net_price"] = net, ["change_pct"] = change,
        };

    [Fact]
    public void Build_reads_invoices_and_trend_with_odoo_false_as_null()
    {
        var rows = new[]
        {
            Row("2026-01-10", "A12423210", 72, 13.50, 15, 11.475, 7.1),
            Row("2024-03-01", "A12279587", 60, 12.00, 10, 10.80, false),
        };
        var trend = new JsonObject
        {
            ["list_price"] = 14.31, ["list_date"] = "2026-06-01", ["first_price"] = 12.0, ["first_date"] = "2024-03-01",
            ["total_change_pct"] = 19.25, ["yearly_change_pct"] = false,
        };

        var s = SupplierPriceHistoryReader.Build(rows, trend);

        Assert.True(s.HasData);
        Assert.Equal(2, s.Invoices.Count);
        Assert.Equal(new DateOnly(2026, 1, 10), s.Invoices[0].InvoiceDate);
        Assert.Equal(13.50m, s.Invoices[0].ListPrice);
        Assert.Equal(7.1m, s.Invoices[0].ChangePct);
        Assert.Null(s.Invoices[1].ChangePct);
        Assert.Equal(14.31m, s.PriceListPrice);
        Assert.Equal(new DateOnly(2026, 6, 1), s.PriceListDate);
        Assert.Equal(19.25m, s.TotalChangePct);
        Assert.Null(s.YearlyChangePct);
    }

    [Fact]
    public void Build_takes_the_price_list_line_for_an_item_never_invoiced()
    {
        var s = SupplierPriceHistoryReader.Build(Array.Empty<JsonObject>(), null, new JsonObject { ["price"] = 53.73 });

        Assert.True(s.HasData);
        Assert.Empty(s.Invoices);
        Assert.Equal(53.73m, s.PriceListPrice);
    }

    [Fact]
    public async Task ReadAsync_is_empty_when_odoo_fails()
    {
        var odoo = new Mock<IOdooRpc>();
        odoo.Setup(o => o.ModelExistsAsync(It.IsAny<string>())).ThrowsAsync(new HttpRequestException("down"));
        var reader = new SupplierPriceHistoryReader(odoo.Object, NullLogger<SupplierPriceHistoryReader>.Instance);

        var s = await reader.ReadAsync("1015");

        Assert.False(s.HasData);
    }

    [Fact]
    public async Task ReadAsync_is_empty_without_the_odoo_module()
    {
        var odoo = new Mock<IOdooRpc>();
        odoo.Setup(o => o.ModelExistsAsync(SupplierPriceHistoryReader.HistoryModel)).ReturnsAsync(false);
        var reader = new SupplierPriceHistoryReader(odoo.Object, NullLogger<SupplierPriceHistoryReader>.Instance);

        var s = await reader.ReadAsync("1015");

        Assert.False(s.HasData);
        odoo.Verify(o => o.SearchReadAsync(It.IsAny<string>(), It.IsAny<JsonArray>(), It.IsAny<IEnumerable<string>>(),
            It.IsAny<int?>(), It.IsAny<string?>()), Times.Never);
    }
}
