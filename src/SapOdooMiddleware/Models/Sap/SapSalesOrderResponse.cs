namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response returned after successfully creating a Sales Order in SAP B1.
/// </summary>
public class SapSalesOrderResponse
{
    /// <summary>SAP Sales Order DocEntry (internal key).</summary>
    public int DocEntry { get; set; }

    /// <summary>SAP Sales Order DocNum (user-facing number).</summary>
    public int DocNum { get; set; }

    /// <summary>
    /// The Odoo SO identifier written onto the SAP SO header UDF <c>U_Odoo_SO_ID</c>
    /// and <c>NumAtCard</c>.
    /// </summary>
    public string UOdooSoId { get; set; } = string.Empty;

    /// <summary>Pick list absolute entry, if a pick list was auto-created.</summary>
    public int? PickListEntry { get; set; }

    /// <summary>
    /// Optional human-readable caveat describing a condition the
    /// caller (ICC) should surface to operators even though the SO
    /// operation itself succeeded.  Populated for example when an
    /// update to an already-synced SO added new lines to ORDR/RDR1,
    /// but the existing pick list (OPKL) could not be refreshed
    /// because it is already Picked or Closed — warehouse staff need
    /// to either add the missing line(s) to the active pick list or
    /// open a new pick list for the additional items.
    /// </summary>
    public string? Warning { get; set; }
}
