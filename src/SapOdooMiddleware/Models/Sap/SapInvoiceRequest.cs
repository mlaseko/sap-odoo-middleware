using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Request payload sent from Odoo to create an AR Invoice in SAP B1.
/// The invoice is created by copying from the related Delivery Note (ODLN),
/// which maintains the Sales Order → Delivery → Invoice document chain.
/// </summary>
public class SapInvoiceRequest
{
    /// <summary>
    /// Odoo invoice reference (account.move name, e.g. "INV/2026/00001").
    /// Stored as <c>NumAtCard</c> on the SAP Invoice and in UDF <c>U_Odoo_Invoice_ID</c>.
    /// </summary>
    [Required]
    public string ExternalInvoiceId { get; set; } = string.Empty;

    /// <summary>
    /// SAP Business Partner (customer) card code (OCRD.CardCode).
    /// </summary>
    [Required]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>
    /// Invoice posting date (ISO-8601). Maps to OINV.DocDate.
    /// </summary>
    public DateTime? DocDate { get; set; }

    /// <summary>
    /// Invoice payment due date (ISO-8601). Maps to OINV.DocDueDate.
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// Invoice currency code (e.g. "USD", "ZAR"). Maps to OINV.DocCur.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>
    /// Total invoice amount including tax. Maps to OINV.DocTotal.
    /// Used for reconciliation validation only — SAP calculates the actual total.
    /// </summary>
    public double? DocTotal { get; set; }

    /// <summary>
    /// Total tax/VAT amount. Maps to OINV.VatSum.
    /// Used for reconciliation validation only — SAP calculates the actual total.
    /// </summary>
    public double? VatSum { get; set; }

    /// <summary>
    /// SAP Delivery Note DocEntry (ODLN.DocEntry) from which to copy this invoice.
    /// When provided, the invoice is created by copying from this delivery,
    /// preserving the Sales Order → Delivery → Invoice document chain in SAP B1.
    /// This is the preferred creation method.
    /// </summary>
    public int? SapDeliveryDocEntry { get; set; }

    /// <summary>
    /// SAP Sales Order DocEntry (ORDR.DocEntry) that originated this invoice.
    /// Available on the delivery document and used for traceability.
    /// When <see cref="SapDeliveryDocEntry"/> is not provided, lines must be supplied.
    /// </summary>
    public int? SapSalesOrderDocEntry { get; set; }

    /// <summary>
    /// Odoo sale.order identifier (e.g. "SO0042").
    /// Stored in SAP B1 header UDF <c>U_Odoo_SO_ID</c> for cross-system traceability.
    /// </summary>
    public string? UOdooSoId { get; set; }

    /// <summary>
    /// Invoice lines. Required when <see cref="SapDeliveryDocEntry"/> is not provided
    /// (manual invoice creation without copy-from-delivery).
    /// When copying from delivery, lines are automatically populated from the delivery note.
    /// </summary>
    public List<SapInvoiceLineRequest> Lines { get; set; } = [];

    /// <summary>
    /// Returns true when the invoice should be created by copying from a delivery document.
    /// </summary>
    [JsonIgnore]
    public bool CopyFromDelivery => SapDeliveryDocEntry.HasValue && SapDeliveryDocEntry.Value > 0;
}

/// <summary>
/// Invoice line item for manual invoice creation (when not copying from delivery).
/// </summary>
public class SapInvoiceLineRequest
{
    /// <summary>SAP item code (OITM.ItemCode). Maps to INV1.ItemCode.</summary>
    [Required]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>Invoiced quantity. Maps to INV1.Quantity.</summary>
    [Range(0.0001, double.MaxValue)]
    public double Quantity { get; set; }

    /// <summary>Unit selling price. Maps to INV1.Price.</summary>
    public double Price { get; set; }

    /// <summary>Line total (before tax). Maps to INV1.LineTotal.</summary>
    public double? LineTotal { get; set; }

    /// <summary>Discount percentage (0-100). Maps to INV1.DiscPrcnt.</summary>
    public double? DiscountPercent { get; set; }

    /// <summary>SAP warehouse code for this line. Maps to INV1.WhsCode.</summary>
    public string? WarehouseCode { get; set; }

    /// <summary>
    /// SAP Delivery Note DocEntry to copy this specific line from.
    /// Used for partial invoicing from a delivery.
    /// Maps to INV1 base document reference (BaseEntry).
    /// </summary>
    public int? BaseDeliveryDocEntry { get; set; }

    /// <summary>
    /// SAP Delivery Note line number to copy from.
    /// Maps to INV1 base document line reference (BaseLine).
    /// </summary>
    public int? BaseDeliveryLineNum { get; set; }

    /// <summary>GL Account code for COGS tracking. Maps to INV1.AcctCode.</summary>
    public string? AccountCode { get; set; }
}
