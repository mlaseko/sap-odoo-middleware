using System.ComponentModel.DataAnnotations;

namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Request payload sent from Odoo to create an Incoming Payment (ORCT) in SAP B1.
/// Supports full and partial payments allocated across one or more AR Invoices.
/// </summary>
public class SapIncomingPaymentRequest
{
    /// <summary>
    /// Odoo payment reference (account.payment name, e.g. "BNK1/2026/00001").
    /// Stored as <c>CounterReference</c> on the SAP Incoming Payment.
    /// </summary>
    [Required]
    public string ExternalPaymentId { get; set; } = string.Empty;

    /// <summary>
    /// SAP Business Partner (customer) card code (OCRD.CardCode).
    /// </summary>
    [Required]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>
    /// Payment posting date (ISO-8601). Maps to ORCT.DocDate.
    /// </summary>
    public DateTime? DocDate { get; set; }

    /// <summary>
    /// Payment currency code (e.g. "TZS", "USD", "EUR"). Maps to ORCT.DocCurrency.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>
    /// Total payment amount. Maps to ORCT.DocTotal / TransferSum or CashSum.
    /// </summary>
    public double PaymentTotal { get; set; }

    /// <summary>
    /// Indicates whether this is a partial payment (i.e. does not fully settle all linked invoices).
    /// </summary>
    public bool IsPartial { get; set; }

    /// <summary>
    /// Odoo journal code or name identifying the payment source
    /// (e.g. "NMB TZS", "CRDB EUR", "Cash My Company").
    /// Used for logging and traceability.
    /// </summary>
    public string? JournalCode { get; set; }

    /// <summary>
    /// SAP G/L account code for the bank or cash account to which this payment is posted
    /// (e.g. "1026217" for NMB Bank TSH, "1026101" for Cash in Hand).
    /// Determines whether <c>CashAccount</c> or <c>TransferAccount</c> is used on the DI API object.
    /// </summary>
    public string? BankOrCashAccountCode { get; set; }

    /// <summary>
    /// When <c>true</c>, the payment is posted to a cash account (<c>CashAccount</c> on the DI API object).
    /// When <c>false</c>, a bank/transfer account is used (<c>TransferAccount</c> / <c>TransferSum</c>).
    /// </summary>
    public bool IsCashPayment { get; set; }

    /// <summary>
    /// SAP G/L account code for the Forex transfer account (default: "1026216").
    /// Set when the payment currency differs from the invoice currency (cross-currency transfer).
    /// When provided, this account is used as <c>CounterAccount</c> / <c>TransferAccount</c> alongside
    /// the primary <c>BankOrCashAccountCode</c>.
    /// </summary>
    public string? ForexAccountCode { get; set; }

    /// <summary>
    /// Odoo database record ID of the account.payment being synced.
    /// Used by the write-back step to update SAP fields on the correct Odoo payment record
    /// after the Incoming Payment is created in SAP B1.
    /// When provided, the middleware writes <c>x_sap_inpay_docentry</c> and
    /// <c>x_sap_inpay_docnum</c> back to Odoo.
    /// </summary>
    public int? OdooPaymentId { get; set; }

    /// <summary>
    /// Optional free-text remarks to store on the SAP Incoming Payment journal entry.
    /// </summary>
    public string? JournalRemarks { get; set; }

    /// <summary>
    /// Invoice allocations for this payment.
    /// Each entry links the payment to one SAP AR Invoice (OINV.DocEntry) with an applied amount.
    /// </summary>
    public List<SapIncomingPaymentLineRequest> Lines { get; set; } = [];
}

/// <summary>
/// Allocation of an Incoming Payment to a single SAP AR Invoice.
/// Maps to the RCT2 (Payments → Invoices) table in SAP B1.
/// </summary>
public class SapIncomingPaymentLineRequest
{
    /// <summary>
    /// SAP AR Invoice DocEntry (OINV.DocEntry) to which this payment is applied.
    /// Maps to RCT2.DocEntry.
    /// </summary>
    [Required]
    public int SapInvoiceDocEntry { get; set; }

    /// <summary>
    /// Amount applied from this payment to the referenced AR Invoice.
    /// Maps to RCT2.SumApplied.
    /// </summary>
    public double AppliedAmount { get; set; }

    /// <summary>
    /// Discount amount granted on this invoice allocation (optional).
    /// Converted to <c>DiscountPercent</c> on the DI API Payments_Invoices object.
    /// Maps to RCT2 TotalDiscount field.
    /// </summary>
    public double? DiscountAmount { get; set; }

    /// <summary>
    /// Odoo database record ID of the invoice (account.move) linked to this allocation.
    /// Stored for traceability; not sent to SAP B1.
    /// </summary>
    public int? OdooInvoiceId { get; set; }
}
