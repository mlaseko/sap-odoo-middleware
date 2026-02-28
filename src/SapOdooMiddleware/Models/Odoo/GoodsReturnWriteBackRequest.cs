namespace SapOdooMiddleware.Models.Odoo;

/// <summary>
/// Request to write SAP Goods Return data back to Odoo after creation in SAP B1.
/// Updates <c>x_sap_return_delivery_docentry</c> on the Odoo return picking (stock.picking).
/// </summary>
public class GoodsReturnWriteBackRequest
{
    /// <summary>Odoo database record ID of the stock.picking (return) to update.</summary>
    public int OdooPickingId { get; set; }

    /// <summary>SAP Goods Return DocEntry (ORDN.DocEntry) to write onto x_sap_return_delivery_docentry.</summary>
    public int SapDocEntry { get; set; }
}
