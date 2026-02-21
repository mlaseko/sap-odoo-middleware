using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Models.Api;

public class DeliveryConfirmationRequest
{
    [JsonPropertyName("odoo_so_ref")]
    public string OdooSoRef { get; set; } = string.Empty;

    [JsonPropertyName("sap_delivery_no")]
    public int SapDeliveryNo { get; set; }

    [JsonPropertyName("delivery_date")]
    public string DeliveryDate { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "delivered";
}

public class DeliveryConfirmationResponse
{
    [JsonPropertyName("odoo_so_ref")]
    public string OdooSoRef { get; set; } = string.Empty;

    [JsonPropertyName("sap_delivery_no")]
    public int SapDeliveryNo { get; set; }

    [JsonPropertyName("odoo_picking_id")]
    public int? OdooPickingId { get; set; }

    [JsonPropertyName("odoo_picking_name")]
    public string? OdooPickingName { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
