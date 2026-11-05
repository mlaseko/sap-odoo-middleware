using System.Text.Json.Nodes;
using SapOdooMiddleware.ItemProvisioning;
using SapOdooMiddleware.Persistence;
using Xunit;

namespace SapOdooMiddleware.Tests.ItemProvisioning;

public class OdooFinalInvoiceGatewayTests
{
    private static StagingDocumentRow Doc(string? salesOrder = "A12423202") => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"), "VR000588529.pdf", "/nonexistent/VR000588529.pdf", "hash", 1000,
        2, "reviewed", "invoice", "LIQUI MOLY GmbH", "VR000588529", new DateTime(2026, 10, 20), salesOrder, null, null,
        null, "eur", 61800m, 3005m, 64805m, 0m, 64805m, "net", new DateTime(2026, 10, 20), "ok", null, null,
        DateTime.UtcNow, DateTime.UtcNow, 2, null, null, null, null, null, 0);

    private static StagingDocumentLineRow Line(int no, string code, decimal qty, decimal price, decimal disc, decimal total,
        bool promo = false, string status = "matched") =>
        new(Guid.NewGuid(), Guid.Empty, no, 1, code, $"Item {code}", "1 l", price, qty, "pcs", "27101981", "DE", disc, total,
            promo, status);

    [Fact]
    public void Payload_carries_every_invoice_line_free_goods_and_display_items_included()
    {
        var lines = new[]
        {
            Line(2, "9985", 1, 36m, 0, 36m, promo: true, status: "skip"),       // display rack, not stock
            Line(1, "2315", 3888, 5.51m, 0, 21422.88m),
            Line(3, "5370", 1000, 0.05m, 100, 0m, promo: true, status: "skip"), // free promotional item
        };
        var p = OdooFinalInvoiceGateway.BuildPayload(Doc(), lines, "JVBERi0=");

        Assert.Equal("A12423202", p["sales_order"]!.GetValue<string>());
        Assert.Equal("VR000588529", p["invoice_number"]!.GetValue<string>());
        Assert.Equal("2026-10-20", p["invoice_date"]!.GetValue<string>());
        Assert.Equal("EUR", p["currency"]!.GetValue<string>());
        Assert.Equal(3005d, p["freight"]!.GetValue<double>());
        Assert.Equal(64805d, p["total"]!.GetValue<double>());
        Assert.Equal("JVBERi0=", p["pdf"]!.GetValue<string>());
        var arr = p["lines"]!.AsArray();
        Assert.Equal(3, arr.Count);
        Assert.Equal("2315", arr[0]!["item_code"]!.GetValue<string>());   // in invoice order
        Assert.Equal(100d, arr[2]!["discount"]!.GetValue<double>());
    }

    [Fact]
    public void Odoo_answer_is_read_with_its_false_values()
    {
        var ok = OdooFinalInvoiceGateway.Parse(JsonNode.Parse(
            """{"ok": true, "error": false, "order_id": 13, "order_name": "A12423202", "container_no": "CMAU0247119", "changes": ["removed: 2315 × 3000 at 5.51 (0% off)"], "unchanged": false}"""));
        Assert.True(ok.Ok);
        Assert.Null(ok.Error);
        Assert.Equal("CMAU0247119", ok.ContainerNo);
        Assert.Single(ok.Changes);

        var refused = OdooFinalInvoiceGateway.Parse(JsonNode.Parse(
            """{"ok": false, "error": "A12423202 already carries invoice VR1, not VR2.", "order_id": 13, "order_name": "A12423202"}"""));
        Assert.False(refused.Ok);
        Assert.Contains("already carries", refused.Error);

        Assert.False(OdooFinalInvoiceGateway.Parse(null).Ok);
    }

    [Fact]
    public void Po_remarks_name_the_odoo_order_and_container()
    {
        Assert.Equal("Odoo A12423202 · container CMAU0247119 · SO: A12423202",
            PurchaseOrderService.BuildComments(Doc(), "A12423202", "CMAU0247119"));
        Assert.Equal("SO: A12423202", PurchaseOrderService.BuildComments(Doc()));
        Assert.Null(PurchaseOrderService.BuildComments(Doc(salesOrder: null)));
    }
}
