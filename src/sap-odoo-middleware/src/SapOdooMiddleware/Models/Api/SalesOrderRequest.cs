using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Models.Api;

public class SalesOrderRequest
{
    [JsonPropertyName("customer_card_code")]
    public string CustomerCardCode { get; set; } = string.Empty;

    [JsonPropertyName("odoo_order_ref")]
    public string OdooOrderRef { get; set; } = string.Empty;

    [JsonPropertyName("order_date")]
    public string? OrderDate { get; set; }

    [JsonPropertyName("due_date")]
    public string? DueDate { get; set; }

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("lines")]
    public List<SalesOrderLineRequest> Lines { get; set; } = new();
}

public class SalesOrderLineRequest
{
    [JsonPropertyName("item_code")]
    public string ItemCode { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("price")]
    public decimal? Price { get; set; }

    [JsonPropertyName("warehouse_code")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("odoo_line_ref")]
    public string? OdooLineRef { get; set; }
}
