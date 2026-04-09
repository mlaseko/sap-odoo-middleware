using System.ComponentModel.DataAnnotations;

namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Request payload sent from Odoo to create an AR Credit Memo (ORIN) in SAP B1.
/// The credit memo reverses an open AR Invoice using the Copy-From mechanism.
/// <see cref="SapBaseInvoiceDocEntry"/> identifies the source invoice; the
/// middleware loads the invoice from SAP, validates it is open, and matches
/// credit lines by ItemCode to resolve the correct BaseLine automatically.
/// </summary>
public class SapCreditMemoRequest
{
    /// <summary>
    /// Odoo credit note reference (account.move name, e.g. "RINV/2026/00001").
    /// Stored as <c>NumAtCard</c> on the SAP Credit Memo and in UDF <c>U_Odoo_Invoice_ID</c>.
    /// </summary>
    [Required]
    public string ExternalCreditMemoId { get; set; } = string.Empty;

    /// <summary>
    /// SAP Business Partner (customer) card code (OCRD.CardCode).
    /// </summary>
    [Required]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>
    /// Credit memo posting date (ISO-8601). Maps to ORIN.DocDate.
    /// </summary>
    public DateTime? DocDate { get; set; }

    /// <summary>
    /// Credit memo due date (ISO-8601). Maps to ORIN.DocDueDate.
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// Currency code (e.g. "USD", "TZS"). Maps to ORIN.DocCur.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>
    /// Total credit amount including tax. For reconciliation validation only.
    /// </summary>
    public double? DocTotal { get; set; }

    /// <summary>
    /// Total tax/VAT amount. For reconciliation validation only.
    /// </summary>
    public double? VatSum { get; set; }

    /// <summary>
    /// SAP AR Invoice DocEntry (OINV.DocEntry) to copy from (required).
    /// The middleware loads this invoice from SAP, verifies it is open,
    /// and matches credit lines by ItemCode to resolve BaseLine automatically.
    /// </summary>
    [Required]
    public int? SapBaseInvoiceDocEntry { get; set; }

    /// <summary>
    /// SAP Sales Order DocEntry (ORDR.DocEntry) for traceability.
    /// </summary>
    public int? SapSalesOrderDocEntry { get; set; }

    /// <summary>
    /// Odoo sale.order identifier (e.g. "SO0042").
    /// Stored in SAP B1 header UDF <c>U_Odoo_SO_ID</c>.
    /// </summary>
    public string? UOdooSoId { get; set; }

    /// <summary>
    /// Odoo database record ID of the account.move (credit note) being synced.
    /// Used by the write-back step to update <c>x_sap_credit_docentry</c> on the
    /// correct Odoo record after the Credit Memo is created in SAP B1.
    /// </summary>
    public int? OdooInvoiceId { get; set; }

    /// <summary>
    /// Credit memo line items. Only ItemCode, Quantity, and Price are required;
    /// the middleware resolves the invoice line number from SAP automatically.
    /// </summary>
    public List<SapCreditMemoLineRequest> Lines { get; set; } = [];
}

/// <summary>
/// Credit memo line item. Only ItemCode, Quantity, and Price are required —
/// the middleware resolves the base invoice line number by matching ItemCode
/// against the source invoice loaded from SAP.
/// </summary>
public class SapCreditMemoLineRequest
{
    /// <summary>SAP item code (OITM.ItemCode). Maps to RIN1.ItemCode.</summary>
    [Required]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>Credit quantity. Maps to RIN1.Quantity.</summary>
    [Range(0.0001, double.MaxValue)]
    public double Quantity { get; set; }

    /// <summary>Unit price. Maps to RIN1.Price.</summary>
    public double Price { get; set; }

    /// <summary>Line total (before tax). Maps to RIN1.LineTotal.</summary>
    public double? LineTotal { get; set; }

    /// <summary>Discount percentage (0-100). Maps to RIN1.DiscPrcnt.</summary>
    public double? DiscountPercent { get; set; }

    /// <summary>SAP warehouse code for this line. Maps to RIN1.WhsCode.</summary>
    public string? WarehouseCode { get; set; }
}
