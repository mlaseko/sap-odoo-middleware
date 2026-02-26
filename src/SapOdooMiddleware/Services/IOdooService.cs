using SapOdooMiddleware.Models.Odoo;

namespace SapOdooMiddleware.Services;

/// <summary>
/// Abstraction over the Odoo JSON-RPC API for delivery-confirmation,
/// invoice write-back, and COGS journal entry operations.
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
    /// Writes SAP Incoming Payment data back to Odoo after creation in SAP B1.
    /// Updates <c>x_sap_inpay_docentry</c> and <c>x_sap_inpay_docnum</c>
    /// on the Odoo payment record (account.payment).
    /// </summary>
    Task UpdateIncomingPaymentAsync(IncomingPaymentWriteBackRequest request);

    /// <summary>
    /// Creates or updates a COGS journal entry in Odoo for a given SAP AR Invoice.
    /// Implements the full flow: find invoice → match lines → compute COGS →
    /// build JE → hash check → create/update → post.
    /// </summary>
    Task<CogsJournalResponse> CreateOrUpdateCogsJournalAsync(CogsJournalRequest request);

    /// <summary>
    /// Verifies Odoo JSON-RPC connectivity by authenticating and returning session info.
    /// Does not modify any data in Odoo.
    /// </summary>
    Task<OdooPingResponse> PingAsync();
}
