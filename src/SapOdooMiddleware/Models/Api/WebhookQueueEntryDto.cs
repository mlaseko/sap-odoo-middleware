namespace SapOdooMiddleware.Models.Api;

/// <summary>
/// Read-only snapshot of an ODOO_WEBHOOK_QUEUE row returned by the queue management endpoints.
/// </summary>
public class WebhookQueueEntryDto
{
    public int Id { get; set; }

    /// <summary>SAP object type: 15 delivery, 22 PO, 20 GRPO, 18 A/P invoice, 19 A/P credit memo.</summary>
    public string ObjectType { get; set; } = string.Empty;
    public int? DocNum { get; set; }
    public string? CardCode { get; set; }
    public int DocEntry { get; set; }
    public string OdooSoId { get; set; } = string.Empty;
    public DateTime? DeliveryDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ResponseBody { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string EventType { get; set; } = "webhook";
}
