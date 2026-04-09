using System.ComponentModel.DataAnnotations;

namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Request payload sent from Odoo to create a Goods Return (ORDN) in SAP B1.
/// The goods return reverses a Delivery Note (ODLN) using the Copy-From mechanism.
/// <see cref="SapBaseDeliveryDocEntry"/> identifies the source delivery; the
/// middleware loads the delivery from SAP and matches return lines by ItemCode.
/// Works with both open and closed deliveries.
/// </summary>
public class SapGoodsReturnRequest
{
    /// <summary>
    /// Odoo return picking reference (stock.picking name, e.g. "WH/RET/00001").
    /// Stored in SAP B1 header UDF <c>U_Odoo_Delivery_ID</c>.
    /// </summary>
    [Required]
    public string ExternalReturnId { get; set; } = string.Empty;

    /// <summary>
    /// SAP Business Partner (customer) card code (OCRD.CardCode).
    /// </summary>
    [Required]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>
    /// Return date (ISO-8601). Maps to ORDN.DocDate.
    /// </summary>
    public DateTime? DeliveryDate { get; set; }

    /// <summary>
    /// SAP Delivery Note DocEntry (ODLN.DocEntry) to copy from (required).
    /// The middleware loads this delivery from SAP and matches return lines
    /// by ItemCode to resolve the correct BaseLine for each return line.
    /// </summary>
    [Required]
    public int? SapBaseDeliveryDocEntry { get; set; }

    /// <summary>
    /// SAP Sales Order DocEntry (ORDR.DocEntry) for traceability.
    /// </summary>
    public int? SalesOrderDocEntry { get; set; }

    /// <summary>
    /// Odoo sale.order identifier (e.g. "SO0042").
    /// Stored in SAP B1 header UDF <c>U_Odoo_SO_ID</c>.
    /// </summary>
    public string? UOdooSoId { get; set; }

    /// <summary>
    /// Odoo database record ID of the stock.picking (return) being synced.
    /// Used by the write-back step to update <c>x_sap_return_delivery_docentry</c>.
    /// </summary>
    public int? OdooPickingId { get; set; }

    /// <summary>
    /// Return line items. ItemCode + Quantity are required; the middleware
    /// resolves the delivery line number from SAP automatically.
    /// </summary>
    public List<SapGoodsReturnLineRequest> Lines { get; set; } = [];
}

/// <summary>
/// Goods return line item.  Only ItemCode and Quantity are required — the
/// middleware resolves the base delivery line number by matching ItemCode
/// against the source delivery loaded from SAP.
/// </summary>
public class SapGoodsReturnLineRequest
{
    /// <summary>SAP item code (OITM.ItemCode). Maps to RDN1.ItemCode.</summary>
    [Required]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>Returned quantity. Maps to RDN1.Quantity.</summary>
    [Range(0.0001, double.MaxValue)]
    public double Quantity { get; set; }

    /// <summary>SAP warehouse code for this line. Maps to RDN1.WhsCode.</summary>
    public string? WarehouseCode { get; set; }
}
