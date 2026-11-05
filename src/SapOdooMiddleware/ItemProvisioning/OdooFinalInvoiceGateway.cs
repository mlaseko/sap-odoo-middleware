using System.Text.Json.Nodes;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.ItemProvisioning;

/// <summary>What Odoo answered to <c>supplier.order.dgx_apply_final_invoice</c>.</summary>
public sealed record OdooFinalInvoiceResult(
    bool Ok,
    string? Error,
    int? OrderId,
    string? OrderName,
    string? ContainerNo,
    IReadOnlyList<string> Changes,
    bool Unchanged);

/// <summary>
/// Phase B of the invoice-to-stock plan (5 Nov 2026): when the DGX app posts the SAP purchase order, the final
/// invoice is first written into its Odoo container order (<c>molas_sap_purchase_icc</c>), which answers with the
/// container number and the order reference the PO carries.
/// </summary>
public interface IOdooFinalInvoiceGateway
{
    Task<OdooFinalInvoiceResult> ApplyAsync(JsonObject invoice);
}

public sealed class OdooFinalInvoiceGateway : IOdooFinalInvoiceGateway
{
    private readonly IOdooRpc _rpc;

    public OdooFinalInvoiceGateway(IOdooRpc rpc) => _rpc = rpc;

    public async Task<OdooFinalInvoiceResult> ApplyAsync(JsonObject invoice)
    {
        var res = await _rpc.CallModelAsync("supplier.order", "dgx_apply_final_invoice",
            new JsonObject { ["invoice"] = invoice });
        return Parse(res);
    }

    /// <summary>Reads Odoo's answer; Odoo writes <c>false</c> for empty values.</summary>
    public static OdooFinalInvoiceResult Parse(JsonNode? res)
    {
        if (res is not JsonObject o)
            return new(false, "Odoo gave no answer for the final invoice.", null, null, null, [], false);
        static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var changes = (o["changes"] as JsonArray)?.Select(Text).Where(s => s is not null).Select(s => s!).ToList() ?? [];
        return new(Bool(o["ok"]), Text(o["error"]), Int(o["order_id"]), Text(o["order_name"]), Text(o["container_no"]),
            changes, Bool(o["unchanged"]));
    }

    /// <summary>
    /// The invoice as Odoo takes it: every line printed on the invoice (free goods and display items too, whatever
    /// their review status, since Odoo holds the whole invoice), the header, the totals and the PDF.
    /// </summary>
    public static JsonObject BuildPayload(StagingDocumentRow doc, IEnumerable<StagingDocumentLineRow> lines, string? pdfBase64)
    {
        static JsonNode? Date(DateTime? d) => d.HasValue ? JsonValue.Create(d.Value.ToString("yyyy-MM-dd")) : null;
        static double Num(decimal? d) => (double)(d ?? 0m);

        var arr = new JsonArray();
        foreach (var l in lines.OrderBy(l => l.PageNo).ThenBy(l => l.LineNo))
        {
            if (string.IsNullOrWhiteSpace(l.ArticleNumber) && string.IsNullOrWhiteSpace(l.Description))
                continue;
            arr.Add(new JsonObject
            {
                ["item_code"] = (l.ArticleNumber ?? "").Trim(),
                ["name"] = (l.Description ?? "").Trim(),
                ["pack_size"] = l.PackSize,
                ["quantity"] = Num(l.Quantity),
                ["uom"] = l.Unit,
                ["unit_price"] = Num(l.UnitPrice),
                ["discount"] = (double)l.DiscountPct,
                ["amount"] = Num(l.LineTotal),
                ["commodity_code"] = l.CommodityCode,
                ["origin"] = l.Origin,
            });
        }

        var subtotal = doc.Subtotal ?? lines.Sum(l => l.LineTotal ?? 0m);
        var total = doc.TotalNet ?? doc.InvoiceTotal;
        return new JsonObject
        {
            ["sales_order"] = doc.SalesOrder?.Trim(),
            ["invoice_number"] = doc.InvoiceNumber?.Trim(),
            ["invoice_date"] = Date(doc.InvoiceDate),
            ["due_date"] = Date(doc.DueDate),
            ["currency"] = doc.Currency?.Trim().ToUpperInvariant(),
            ["subtotal"] = (double)subtotal,
            ["freight"] = Num(doc.Freight),
            ["total"] = total.HasValue ? (double)total.Value : null,
            ["lines"] = arr,
            ["pdf"] = pdfBase64,
            ["filename"] = doc.OriginalFilename,
            ["dgx_document_id"] = doc.Id.ToString(),
        };
    }
}
