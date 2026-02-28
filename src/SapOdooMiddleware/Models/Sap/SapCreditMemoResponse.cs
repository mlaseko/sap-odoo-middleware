namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response returned after successfully creating an AR Credit Memo (ORIN) in SAP B1.
/// </summary>
public class SapCreditMemoResponse
{
    /// <summary>SAP Credit Memo DocEntry (internal key). Maps to ORIN.DocEntry.</summary>
    public int DocEntry { get; set; }

    /// <summary>SAP Credit Memo DocNum (user-facing number). Maps to ORIN.DocNum.</summary>
    public int DocNum { get; set; }

    /// <summary>
    /// Odoo credit note reference (account.move name).
    /// Echoed back from the request for end-to-end traceability.
    /// </summary>
    public string? ExternalCreditMemoId { get; set; }

    /// <summary>
    /// Odoo database record ID of the credit note that was synced.
    /// Echoed back from the request for correlation.
    /// </summary>
    public int? OdooInvoiceId { get; set; }

    /// <summary>
    /// Whether the Odoo write-back (x_sap_credit_docentry) succeeded.
    /// Null when write-back was not attempted.
    /// </summary>
    public bool? OdooWriteBackSuccess { get; set; }

    /// <summary>
    /// Error message if the Odoo write-back failed. Null on success or when not attempted.
    /// </summary>
    public string? OdooWriteBackError { get; set; }
}
