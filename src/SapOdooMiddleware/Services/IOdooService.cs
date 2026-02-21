using SapOdooMiddleware.Models.Odoo;

namespace SapOdooMiddleware.Services;

/// <summary>
/// Abstraction over the Odoo JSON-RPC API for delivery-confirmation operations.
/// </summary>
public interface IOdooService
{
    /// <summary>
    /// Confirms a delivery in Odoo: finds the sale.order → stock.picking, reserves,
    /// sets quantities, validates, and writes the SAP delivery reference.
    /// </summary>
    Task<DeliveryUpdateResponse> ConfirmDeliveryAsync(DeliveryUpdateRequest request);
}
