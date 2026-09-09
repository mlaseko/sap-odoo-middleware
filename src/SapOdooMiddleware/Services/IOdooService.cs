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
    /// Updates <c>x_sap_incoming_payment_docentry</c> and <c>x_sap_incoming_payment_docnum</c>
    /// Updates <c>x_sap_inpay_docentry</c> and <c>x_sap_inpay_docnum</c>
    /// on the Odoo payment record (account.payment).
    /// </summary>
    Task UpdateIncomingPaymentAsync(IncomingPaymentWriteBackRequest request);

    /// <summary>
    /// Writes SAP Credit Memo data back to Odoo after creation in SAP B1.
    /// Updates <c>x_sap_credit_docentry</c> on the Odoo credit note (account.move).
    /// </summary>
    Task UpdateCreditMemoAsync(CreditMemoWriteBackRequest request);

    /// <summary>
    /// Writes SAP Goods Return data back to Odoo after creation in SAP B1.
    /// Updates <c>x_sap_return_delivery_docentry</c> on the Odoo return picking (stock.picking).
    /// </summary>
    Task UpdateGoodsReturnAsync(GoodsReturnWriteBackRequest request);

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

    /// <summary>
    /// Pushes updated Lubes price tiers (net TZS) to Odoo: sets the product's
    /// list_price to Retail (matching provisioning) and updates/creates fixed-price
    /// rules on the pricelists mapped in <c>Pricing:OdooPricelistNames</c>.
    /// Best-effort — per-tier failures are reported in the returned notes, not thrown.
    /// </summary>
    Task<List<string>> UpdateLubesPricesAsync(
        string itemCode, decimal retailNet, decimal dealerNet, decimal superDealerNet, decimal maasaiNet);

    /// <summary>
    /// Moves the Odoo product (matched by default_code = item code) into a product
    /// category, resolved in order: <paramref name="categoryExternalId"/> via
    /// ir.model.data (exact, naming-independent — preferred), then the category's
    /// complete name, then the last path segment. Best-effort — outcomes come back
    /// as notes.
    /// </summary>
    Task<List<string>> UpdateLubesCategoryAsync(
        string itemCode, string? categoryFullPath, string? categoryExternalId);
}
