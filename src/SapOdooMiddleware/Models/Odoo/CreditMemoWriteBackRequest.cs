namespace SapOdooMiddleware.Models.Odoo;

/// <summary>
/// Request to write SAP Credit Memo data back to Odoo after creation in SAP B1.
/// Updates <c>x_sap_credit_docentry</c> on the Odoo credit note (account.move).
/// </summary>
public class CreditMemoWriteBackRequest
{
    /// <summary>Odoo database record ID of the account.move (credit note) to update.</summary>
    public int OdooInvoiceId { get; set; }

    /// <summary>SAP Credit Memo DocEntry (ORIN.DocEntry) to write onto x_sap_credit_docentry.</summary>
    public int SapDocEntry { get; set; }

    /// <summary>
    /// SAP Sales Order DocEntry (ORDR.DocEntry) from the original document chain.
    /// Written to x_sap_docentry for complete document chain traceability.
    /// </summary>
    public int? SapSalesOrderDocEntry { get; set; }

    /// <summary>
    /// SAP Delivery DocEntry (ODLN.DocEntry) from the original document chain.
    /// Written to x_sap_delivery_docentry for complete document chain traceability.
    /// </summary>
    public int? SapDeliveryDocEntry { get; set; }

    /// <summary>
    /// SAP Invoice DocEntry (OINV.DocEntry) of the original invoice being reversed.
    /// Written to x_sap_invoice_docentry for complete document chain traceability.
    /// </summary>
    public int? SapBaseInvoiceDocEntry { get; set; }
}
