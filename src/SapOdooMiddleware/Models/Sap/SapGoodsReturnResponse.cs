namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response returned after successfully creating a Goods Return (ORDN) in SAP B1.
/// </summary>
public class SapGoodsReturnResponse
{
    /// <summary>SAP Goods Return DocEntry (internal key). Maps to ORDN.DocEntry.</summary>
    public int DocEntry { get; set; }

    /// <summary>SAP Goods Return DocNum (user-facing number). Maps to ORDN.DocNum.</summary>
    public int DocNum { get; set; }

    /// <summary>
    /// Odoo return picking reference (stock.picking name).
    /// Echoed back from the request for end-to-end traceability.
    /// </summary>
    public string? ExternalReturnId { get; set; }

    /// <summary>
    /// Odoo database record ID of the stock.picking that was synced.
    /// Echoed back from the request for correlation.
    /// </summary>
    public int? OdooPickingId { get; set; }

    /// <summary>
    /// Whether the Odoo write-back (x_sap_return_delivery_docentry) succeeded.
    /// Null when write-back was not attempted.
    /// </summary>
    public bool? OdooWriteBackSuccess { get; set; }

    /// <summary>
    /// Error message if the Odoo write-back failed. Null on success or when not attempted.
    /// </summary>
    public string? OdooWriteBackError { get; set; }
}
