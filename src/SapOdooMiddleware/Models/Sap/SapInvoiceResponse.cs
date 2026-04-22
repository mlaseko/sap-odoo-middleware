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

    /// <summary>
    /// COGS journal entry action: "created", "updated", "skipped", or "failed".
    /// Null when COGS was not attempted (no OdooInvoiceId or no lines).
    /// </summary>
    public string? CogsJournalAction { get; set; }

    /// <summary>
    /// Odoo account.move ID of the COGS journal entry (when created/updated).
    /// </summary>
    public int? CogsJournalEntryId { get; set; }

    /// <summary>
    /// Error message if the COGS journal creation failed. Null on success.
    /// </summary>
    public string? CogsJournalError { get; set; }

    /// <summary>
    /// Machine-readable per-line caveats.  Each entry describes a
    /// condition the caller (ICC) should surface to operators while
    /// the invoice operation itself succeeded.  Today only populated
    /// for bin-shortfall cases on pure-manual invoice lines (no
    /// copy-from-delivery): the invoice is always posted to SAP
    /// regardless of Warnings; any pure-manual line whose priority
    /// bins cannot fully cover the required quantity is reported
    /// here so the warehouse can reconcile manually.
    /// </summary>
    public List<InvoiceWarning> Warnings { get; set; } = new();
}

/// <summary>
/// Structured warning about one invoice condition the caller should
/// surface to operators.  Parallels <see cref="SalesOrderWarning"/>
/// on the SO flow so ICC-side consumers can share the same parsing
/// shape across both document types.
/// </summary>
public class InvoiceWarning
{
    /// <summary>Stable machine-readable code.  Known codes:
    /// <list type="bullet">
    ///   <item><c>BIN_SHORTFALL</c> — an invoice line couldn't be
    ///     fully covered by bin stock in its warehouse; the invoice
    ///     line was posted but SAP did not receive an explicit bin
    ///     allocation and will need manual reconciliation.</item>
    /// </list>
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>SAP item code for the affected line, when applicable.</summary>
    public string? ItemCode { get; set; }

    /// <summary>Position in the original request.Lines array (zero-based).</summary>
    public int? LineNum { get; set; }

    /// <summary>SAP warehouse the invoice line was posted to.</summary>
    public string? WarehouseCode { get; set; }

    /// <summary>Requested quantity on the line.</summary>
    public double? Required { get; set; }

    /// <summary>Quantity actually allocated across bins (may be partial).</summary>
    public double? Allocated { get; set; }

    /// <summary>Human-readable explanation.</summary>
    public string Message { get; set; } = string.Empty;
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
