namespace SapOdooMiddleware.Models.Odoo;

/// <summary>
/// Request to write SAP Incoming Payment data back to Odoo after creation in SAP B1.
/// Updates <c>x_sap_incoming_payment_docentry</c> and <c>x_sap_incoming_payment_docnum</c>
/// Updates <c>x_sap_inpay_docentry</c> and <c>x_sap_inpay_docnum</c>
/// on the Odoo payment record (account.payment).
/// </summary>
public class IncomingPaymentWriteBackRequest
{
    /// <summary>Odoo database record ID of the account.payment to update.</summary>
    public int OdooPaymentId { get; set; }

    /// <summary>SAP Incoming Payment DocEntry (ORCT.DocEntry) to write onto x_sap_inpay_docentry.</summary>
    public int SapDocEntry { get; set; }

    /// <summary>SAP Incoming Payment DocNum (ORCT.DocNum) to write onto x_sap_inpay_docnum.</summary>
    public int SapDocNum { get; set; }
}
