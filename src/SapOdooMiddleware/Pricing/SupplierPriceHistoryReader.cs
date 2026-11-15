using System.Globalization;
using System.Text.Json.Nodes;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Pricing;

/// <summary>One LIQUI MOLY invoice of an item, as Odoo's Price History (molas_supplier_orders) gives it.</summary>
public sealed record SupplierInvoicePrice(
    DateOnly? InvoiceDate, string? InvoiceNo, decimal Quantity, decimal ListPrice, decimal Discount, decimal NetPrice,
    decimal? ChangePct);

/// <summary>
/// An item's supplier prices for /pricing: its LIQUI MOLY invoices (latest first) and its trend, with the price of
/// the active LIQUI MOLY price list. All in EUR.
/// </summary>
public sealed record SupplierPriceSummary(
    IReadOnlyList<SupplierInvoicePrice> Invoices,
    decimal? PriceListPrice, DateOnly? PriceListDate,
    decimal? FirstPrice, DateOnly? FirstDate, decimal? TotalChangePct, decimal? YearlyChangePct)
{
    public static readonly SupplierPriceSummary Empty = new(Array.Empty<SupplierInvoicePrice>(), null, null, null, null, null, null);
    public bool HasData => Invoices.Count > 0 || PriceListPrice is not null;
}

/// <summary>
/// Reads the supplier price history of an item from Odoo (Molas Imports &amp; Finance: the read-only views
/// supplier.item.price.history and supplier.item.price.trend, and supplier.price.list.line for an item never
/// invoiced). Read only and best-effort: /pricing works the same when Odoo or the module is not there.
/// </summary>
public class SupplierPriceHistoryReader
{
    public const string HistoryModel = "supplier.item.price.history";
    public const string TrendModel = "supplier.item.price.trend";
    public const string ListLineModel = "supplier.price.list.line";

    private static readonly string[] HistoryFields =
        { "invoice_date", "invoice_no", "quantity", "unit_price", "discount", "net_price", "change_pct" };
    private static readonly string[] TrendFields =
        { "list_price", "list_date", "first_price", "first_date", "total_change_pct", "yearly_change_pct" };

    private readonly IOdooRpc _odoo;
    private readonly ILogger<SupplierPriceHistoryReader> _logger;

    public SupplierPriceHistoryReader(IOdooRpc odoo, ILogger<SupplierPriceHistoryReader> logger)
    {
        _odoo = odoo;
        _logger = logger;
    }

    public async Task<SupplierPriceSummary> ReadAsync(string? itemCode, int invoices = 12)
    {
        if (string.IsNullOrWhiteSpace(itemCode)) return SupplierPriceSummary.Empty;
        try
        {
            if (!await _odoo.ModelExistsAsync(HistoryModel)) return SupplierPriceSummary.Empty;
            var byItem = new JsonArray { new JsonArray("item_code", "=ilike", itemCode!.Trim()) };
            var rows = await _odoo.SearchReadAsync(HistoryModel, byItem, HistoryFields, invoices, "invoice_date desc, id desc");
            var trend = (await _odoo.SearchReadAsync(TrendModel, Clone(byItem), TrendFields, 1)).FirstOrDefault();
            JsonObject? listLine = null;
            if (trend is null)
            {
                var activeList = new JsonArray
                {
                    new JsonArray("item_code", "=ilike", itemCode!.Trim()), new JsonArray("state", "=", "active"),
                };
                listLine = (await _odoo.SearchReadAsync(ListLineModel, activeList, new[] { "price", "list_id" }, 1)).FirstOrDefault();
            }
            return Build(rows, trend, listLine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Supplier price history of {Item} not read from Odoo", itemCode);
            return SupplierPriceSummary.Empty;
        }
    }

    /// <summary>The summary from Odoo's rows (false for an empty field, as Odoo sends it).</summary>
    public static SupplierPriceSummary Build(IEnumerable<JsonObject> rows, JsonObject? trend, JsonObject? listLine = null)
    {
        var invoices = rows.Select(r => new SupplierInvoicePrice(
            Date(r["invoice_date"]), Text(r["invoice_no"]), Number(r["quantity"]) ?? 0m, Number(r["unit_price"]) ?? 0m,
            Number(r["discount"]) ?? 0m, Number(r["net_price"]) ?? 0m, Number(r["change_pct"]))).ToList();
        if (trend is null)
            return new SupplierPriceSummary(invoices, listLine is null ? null : Number(listLine["price"]), null,
                null, null, null, null);
        return new SupplierPriceSummary(invoices, Number(trend["list_price"]), Date(trend["list_date"]),
            Number(trend["first_price"]), Date(trend["first_date"]), Number(trend["total_change_pct"]),
            Number(trend["yearly_change_pct"]));
    }

    private static JsonArray Clone(JsonArray a) => (JsonArray)JsonNode.Parse(a.ToJsonString())!;

    /// <summary>A number, or null for Odoo's false / null. A list price of 0 means none.</summary>
    internal static decimal? Number(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var d)) return d;
        if (v.TryGetValue<double>(out var f)) return (decimal)f;
        if (v.TryGetValue<long>(out var l)) return l;
        return null;
    }

    internal static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static DateOnly? Date(JsonNode? n)
        => Text(n) is { } s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
}
