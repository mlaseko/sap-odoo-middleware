namespace SapOdooMiddleware.Models.Api;

/// <summary>
/// Read-only snapshot of an ODOO_WEBHOOK_QUEUE row returned by the queue management endpoints.
/// </summary>
public class WebhookQueueEntryDto
{
    public int Id { get; set; }
    public int DocEntry { get; set; }
    public string OdooSoId { get; set; } = string.Empty;
    public DateTime? DeliveryDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ResponseBody { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
