namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response returned when querying the document status of a Return Request (ORRR) in SAP B1.
/// Used by Odoo to gate return validation — the return picking can only be validated
/// in Odoo once the Return Request is closed in SAP (inventory adjusted).
/// </summary>
public class SapReturnRequestStatusResponse
{
    /// <summary>SAP Return Request DocEntry (internal key). Maps to ORRR.DocEntry.</summary>
    public int DocEntry { get; set; }

    /// <summary>SAP Return Request DocNum (user-facing number). Maps to ORRR.DocNum.</summary>
    public int DocNum { get; set; }

    /// <summary>
    /// Document status: "open" or "closed".
    /// A Return Request is "closed" when SAP has fully processed the return
    /// (goods received back into inventory).  Odoo should only validate the
    /// return picking once the status is "closed".
    /// </summary>
    public string Status { get; set; } = string.Empty;
}
