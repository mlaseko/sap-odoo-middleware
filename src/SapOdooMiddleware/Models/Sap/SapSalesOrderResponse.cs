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

    /// <summary>The Odoo reference that was written onto the SAP SO header.</summary>
    public string OdooSoRef { get; set; } = string.Empty;

    /// <summary>Pick list absolute entry, if a pick list was auto-created.</summary>
    public int? PickListEntry { get; set; }
}
