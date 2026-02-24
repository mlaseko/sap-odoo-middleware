namespace SapOdooMiddleware.Models.Odoo;

/// <summary>
/// Payload sent to the Odoo Integration Control Center to report
/// delivery webhook processing status.
/// </summary>
public class DeliveryMonitorPayload
{
    /// <summary>Shared API key for authentication.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>"api" for direct API calls, "queue" for WebhookQueueProcessor.</summary>
    public string Source { get; set; } = "api";

    /// <summary>The Odoo sale.order identifier (e.g. "SO0042").</summary>
    public string OdooSoId { get; set; } = string.Empty;

    /// <summary>SAP Delivery Note number / DocEntry.</summary>
    public string SapDeliveryNo { get; set; } = string.Empty;

    /// <summary>Delivery date (ISO-8601), if available.</summary>
    public string? DeliveryDate { get; set; }

    /// <summary>SQL Server queue entry Id (only for queue-sourced).</summary>
    public int? QueueEntryId { get; set; }

    /// <summary>"pending", "processing", "done", or "failed".</summary>
    public string State { get; set; } = "pending";

    /// <summary>Odoo stock.picking id.</summary>
    public int? PickingId { get; set; }

    /// <summary>Odoo stock.picking name.</summary>
    public string? PickingName { get; set; }

    /// <summary>Final picking state after validation.</summary>
    public string? PickingState { get; set; }

    /// <summary>Error details if the delivery confirmation failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Number of retry attempts.</summary>
    public int RetryCount { get; set; }

    /// <summary>When the middleware finished processing.</summary>
    public string? ProcessedAt { get; set; }

    /// <summary>Processing duration in seconds.</summary>
    public double? Duration { get; set; }
}
