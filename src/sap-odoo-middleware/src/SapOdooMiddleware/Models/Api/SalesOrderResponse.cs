using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Models.Api;

public class SalesOrderResponse
{
    [JsonPropertyName("doc_entry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("doc_num")]
    public int DocNum { get; set; }

    [JsonPropertyName("odoo_order_ref")]
    public string OdooOrderRef { get; set; } = string.Empty;

    [JsonPropertyName("customer_card_code")]
    public string CustomerCardCode { get; set; } = string.Empty;

    [JsonPropertyName("doc_date")]
    public string DocDate { get; set; } = string.Empty;

    [JsonPropertyName("doc_total")]
    public decimal DocTotal { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}
