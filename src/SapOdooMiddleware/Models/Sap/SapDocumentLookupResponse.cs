namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response from a SAP document lookup by Odoo reference (UDF search).
/// Used by the SAP Field Sync page to find SAP DocEntry/DocNum for
/// Odoo documents that are missing SAP identifiers.
/// </summary>
public class SapDocumentLookupResponse
{
    /// <summary>SAP document internal key (DocEntry).</summary>
    public int DocEntry { get; set; }

    /// <summary>SAP document number (DocNum) — the user-visible number.</summary>
    public int DocNum { get; set; }

    /// <summary>Document status: "open" or "closed".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>SAP Business Partner card code (OCRD.CardCode).</summary>
    public string CardCode { get; set; } = string.Empty;

    /// <summary>The Odoo reference that was searched (e.g. "SO0042", "WH/OUT/000106").</summary>
    public string OdooRef { get; set; } = string.Empty;

    /// <summary>
    /// SAP Pick List absolute entry (OPKL.AbsEntry), if one exists
    /// for this document's sales order chain.
    /// </summary>
    public int? PickListEntry { get; set; }
}
