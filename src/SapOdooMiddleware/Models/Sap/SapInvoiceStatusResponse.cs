namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response returned when querying the document status of an AR Invoice in SAP B1.
/// </summary>
public class SapInvoiceStatusResponse
{
    /// <summary>SAP Invoice DocEntry (internal key). Maps to OINV.DocEntry.</summary>
    public int DocEntry { get; set; }

    /// <summary>SAP Invoice DocNum (user-facing number). Maps to OINV.DocNum.</summary>
    public int DocNum { get; set; }

    /// <summary>
    /// Document status: "open" or "closed".
    /// An invoice is "closed" when it has been fully paid or cancelled.
    /// Credit memos can only be created against open invoices.
    /// </summary>
    public string Status { get; set; } = string.Empty;
}
