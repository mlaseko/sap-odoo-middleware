using SapOdooMiddleware.Models.Odoo;

namespace SapOdooMiddleware.Services;

/// <summary>
/// Abstraction over the Odoo JSON-RPC API for delivery-confirmation
/// and invoice write-back operations.
/// </summary>
public interface IOdooService
{
    /// <summary>
    /// Confirms a delivery in Odoo: finds the sale.order → stock.picking, reserves,
    /// sets quantities, validates, and writes the SAP delivery reference.
    /// </summary>
    Task<DeliveryUpdateResponse> ConfirmDeliveryAsync(DeliveryUpdateRequest request);

    /// <summary>
    /// Writes SAP invoice data back to Odoo after AR Invoice creation in SAP B1.
    /// Updates <c>account.move.x_sap_invoice_docentry</c> on the invoice header and
    /// <c>x_sap_invoice_linenum</c> / <c>x_sap_gross_buy_price</c> on each invoice line.
    /// </summary>
    Task<InvoiceWriteBackResponse> UpdateInvoiceSapFieldsAsync(InvoiceWriteBackRequest request);

    /// <summary>
    /// Verifies Odoo JSON-RPC connectivity by authenticating and returning session info.
    /// Does not modify any data in Odoo.
    /// </summary>
    Task<OdooPingResponse> PingAsync();
}
