using System.ComponentModel.DataAnnotations;

namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Request payload sent from Odoo to create a Goods Return (ORDN) in SAP B1.
/// The goods return reverses a Delivery Note (ODLN) to return items to inventory.
/// When <see cref="Lines"/> contain <see cref="SapGoodsReturnLineRequest.BaseDeliveryDocEntry"/>,
/// the return is created by copying from the original delivery.
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
    /// Return line items.
    /// </summary>
    public List<SapGoodsReturnLineRequest> Lines { get; set; } = [];
}

/// <summary>
/// Goods return line item.  When <see cref="BaseDeliveryDocEntry"/> and
/// <see cref="BaseDeliveryLineNum"/> are provided, the line is created by
/// copying from the original Delivery Note line (DLN1).
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

    /// <summary>
    /// Original Delivery Note DocEntry for copy-from per line.
    /// Maps to RDN1 BaseEntry with BaseType=15 (Delivery Note).
    /// </summary>
    public int? BaseDeliveryDocEntry { get; set; }

    /// <summary>
    /// Original Delivery Note line number for copy-from per line.
    /// Maps to RDN1 BaseLine.
    /// </summary>
    public int? BaseDeliveryLineNum { get; set; }
}
