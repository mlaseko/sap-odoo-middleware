namespace SapOdooMiddleware.Models.Odoo;

/// <summary>
/// Request to write SAP invoice data back to Odoo after AR Invoice creation in SAP B1.
/// Updates <c>account.move.x_sap_invoice_docentry</c> and per-line
/// <c>x_sap_invoice_linenum</c> / <c>x_sap_gross_buy_price</c>.
/// </summary>
public class InvoiceWriteBackRequest
{
    /// <summary>Odoo database record ID of the account.move (invoice).</summary>
    public int OdooInvoiceId { get; set; }

    /// <summary>SAP Invoice DocEntry (OINV.DocEntry) to write onto x_sap_invoice_docentry.</summary>
    public int SapDocEntry { get; set; }

    /// <summary>
    /// Line-level data to write back. Each entry is matched to the corresponding
    /// Odoo invoice line by position (first SAP line → first Odoo line, etc.).
    /// </summary>
    public List<InvoiceLineWriteBack> Lines { get; set; } = [];
}

/// <summary>
/// Per-line SAP data to write back to an Odoo account.move.line.
/// </summary>
public class InvoiceLineWriteBack
{
    /// <summary>SAP invoice line number (0-based). Written to x_sap_invoice_linenum.</summary>
    public int SapLineNum { get; set; }

    /// <summary>
    /// SAP gross buying price (INV1.GrossBuyPr). Written to x_sap_gross_buy_price.
    /// Used downstream for COGS journal entry creation.
    /// </summary>
    public double GrossBuyPrice { get; set; }
}
