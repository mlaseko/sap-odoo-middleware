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
}
