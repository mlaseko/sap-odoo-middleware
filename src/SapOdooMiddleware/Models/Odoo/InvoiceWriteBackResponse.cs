namespace SapOdooMiddleware.Models.Odoo;

/// <summary>
/// Response from writing SAP invoice data back to Odoo.
/// </summary>
public class InvoiceWriteBackResponse
{
    /// <summary>Odoo account.move record ID that was updated.</summary>
    public int OdooInvoiceId { get; set; }

    /// <summary>SAP DocEntry written to x_sap_invoice_docentry.</summary>
    public int SapDocEntry { get; set; }

    /// <summary>Number of Odoo invoice lines updated with SAP line numbers.</summary>
    public int LinesUpdated { get; set; }

    /// <summary>Whether the write-back completed successfully.</summary>
    public bool Success { get; set; }
}
