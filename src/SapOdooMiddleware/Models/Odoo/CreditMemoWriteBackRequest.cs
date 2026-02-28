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
}
