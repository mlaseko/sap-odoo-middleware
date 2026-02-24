using SapOdooMiddleware.Models.Odoo;

namespace SapOdooMiddleware.Services;

/// <summary>
/// Sends delivery webhook status notifications to the Odoo Integration
/// Control Center so that all delivery confirmation activity is tracked.
/// </summary>
public interface IDeliveryMonitorService
{
    /// <summary>
    /// Notify the monitor that a delivery webhook is being processed.
    /// </summary>
    Task NotifyAsync(DeliveryMonitorPayload payload);
}
