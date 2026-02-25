namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response returned after successfully creating an AR Invoice in SAP B1.
/// </summary>
public class SapInvoiceResponse
{
    /// <summary>SAP Invoice DocEntry (internal key). Maps to OINV.DocEntry.</summary>
    public int DocEntry { get; set; }

    /// <summary>SAP Invoice DocNum (user-facing number). Maps to OINV.DocNum.</summary>
    public int DocNum { get; set; }

    /// <summary>
    /// The Odoo invoice reference written onto the SAP Invoice header
    /// as <c>NumAtCard</c> and UDF <c>U_Odoo_Invoice_ID</c>.
    /// </summary>
    public string ExternalInvoiceId { get; set; } = string.Empty;

    /// <summary>
    /// SAP Delivery Note DocEntry that this invoice was copied from (if applicable).
    /// </summary>
    public int? BaseDeliveryDocEntry { get; set; }

    /// <summary>
    /// SAP Sales Order DocEntry referenced by the originating delivery/invoice.
    /// </summary>
    public int? BaseSalesOrderDocEntry { get; set; }

    /// <summary>
    /// Line-level data read back from the created SAP Invoice (INV1).
    /// Each entry contains the SAP LineNum and GrossBuyPrice for COGS tracking.
    /// </summary>
    public List<SapInvoiceLineResponse> Lines { get; set; } = [];

    /// <summary>
    /// Whether the Odoo write-back (x_sap_invoice_docentry + per-line fields) succeeded.
    /// Null when write-back was not attempted (no OdooInvoiceId provided).
    /// </summary>
    public bool? OdooWriteBackSuccess { get; set; }

    /// <summary>
    /// Error message if the Odoo write-back failed. Null on success or when not attempted.
    /// </summary>
    public string? OdooWriteBackError { get; set; }
}

/// <summary>
/// Line-level data read back from a created SAP AR Invoice (INV1 table).
/// </summary>
public class SapInvoiceLineResponse
{
    /// <summary>SAP invoice line number (0-based). Maps to INV1.LineNum.</summary>
    public int LineNum { get; set; }

    /// <summary>SAP item code on this line. Maps to INV1.ItemCode.</summary>
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>Invoiced quantity. Maps to INV1.Quantity.</summary>
    public double Quantity { get; set; }

    /// <summary>
    /// Gross buying price (cost) from SAP B1. Maps to INV1.GrossBuyPr.
    /// Used for COGS journal entry creation in Odoo.
    /// </summary>
    public double GrossBuyPrice { get; set; }
}
