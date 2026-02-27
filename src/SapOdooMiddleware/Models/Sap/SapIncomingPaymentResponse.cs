namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response returned after successfully creating an Incoming Payment (ORCT) in SAP B1.
/// </summary>
public class SapIncomingPaymentResponse
{
    /// <summary>SAP Incoming Payment DocEntry (internal key). Maps to ORCT.DocEntry.</summary>
    public int DocEntry { get; set; }

    /// <summary>SAP Incoming Payment DocNum (user-facing number). Maps to ORCT.DocNum.</summary>
    public int DocNum { get; set; }

    /// <summary>
    /// Odoo payment reference (account.payment name, e.g. "BNK1/2026/00001").
    /// Echoed back from the request for end-to-end traceability.
    /// </summary>
    public string? ExternalPaymentId { get; set; }

    /// <summary>
    /// Odoo database record ID of the payment that was synced.
    /// Echoed back from the request for correlation.
    /// </summary>
    public int? OdooPaymentId { get; set; }

    /// <summary>
    /// Total amount applied across all invoice allocations (sum of SumApplied on RCT2 lines).
    /// </summary>
    public double TotalApplied { get; set; }

    /// <summary>
    /// Whether the Odoo write-back (x_sap_incoming_payment_docentry + x_sap_incoming_payment_docnum) succeeded.
    /// Whether the Odoo write-back (x_sap_inpay_docentry + x_sap_inpay_docnum) succeeded.
    /// Null when write-back was not attempted (no OdooPaymentId provided).
    /// </summary>
    public bool? OdooWriteBackSuccess { get; set; }

    /// <summary>
    /// Error message if the Odoo write-back failed. Null on success or when not attempted.
    /// The SAP Incoming Payment was still created successfully when this field is set.
    /// </summary>
    public string? OdooWriteBackError { get; set; }
}
