using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Request payload sent from Odoo to create a Sales Order in SAP B1.
/// </summary>
public class SapSalesOrderRequest
{
    /// <summary>
    /// Odoo sale.order identifier stored in SAP B1 header UDF <c>U_Odoo_SO_ID</c>
    /// and used as <c>NumAtCard</c> on the SAP Sales Order.
    /// Maps to JSON field <c>u_odoo_so_id</c>.
    /// </summary>
    [Required]
    public string UOdooSoId { get; set; } = string.Empty;

    /// <summary>
    /// [Deprecated] Use <c>u_odoo_so_id</c> instead.
    /// Accepted for backwards compatibility; ignored when <c>u_odoo_so_id</c> is present.
    /// </summary>
    public string? OdooSoRef { get; set; }

    /// <summary>SAP Business Partner (customer) card code.</summary>
    [Required]
    public string CardCode { get; set; } = string.Empty;

    /// <summary>Requested delivery / document date (ISO-8601).</summary>
    public DateTime? DocDate { get; set; }

    /// <summary>Requested delivery due date (ISO-8601).</summary>
    public DateTime? DocDueDate { get; set; }

    /// <summary>Order lines.</summary>
    [Required]
    [MinLength(1)]
    public List<SapSalesOrderLineRequest> Lines { get; set; } = [];

    /// <summary>
    /// Odoo delivery note reference (stock.picking name, e.g. <c>WH/OUT/00011</c>).
    /// Stored in SAP B1 header UDF <c>U_Odoo_Delivery_ID</c>.
    /// Maps to JSON field <c>odoo_delivery_id</c>.
    /// </summary>
    [JsonPropertyName("odoo_delivery_id")]
    public string? OdooDeliveryId { get; set; }

    /// <summary>
    /// Odoo delivery note reference (stock.picking name, e.g. <c>WH/OUT/00011</c>).
    /// Stored in SAP B1 header UDF <c>U_Odoo_Delivery_ID</c>.
    /// Maps to JSON field <c>name</c>.
    /// Accepted for backwards compatibility; <c>odoo_delivery_id</c> takes precedence when present.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Returns the effective Odoo SO identifier: <c>UOdooSoId</c> if set,
    /// otherwise falls back to the deprecated <c>OdooSoRef</c>.
    /// </summary>
    [JsonIgnore]
    public string ResolvedSoId =>
        !string.IsNullOrEmpty(UOdooSoId) ? UOdooSoId : (OdooSoRef ?? string.Empty);

    /// <summary>
    /// Returns the effective Odoo delivery note reference, checked in priority order:
    /// <c>OdooDeliveryId</c> (JSON: <c>odoo_delivery_id</c>), then header-level <c>Name</c>,
    /// then the first non-empty line-level <c>UOdooDeliveryId</c>.
    /// </summary>
    [JsonIgnore]
    public string? ResolvedDeliveryId =>
        !string.IsNullOrEmpty(OdooDeliveryId)
            ? OdooDeliveryId
            : !string.IsNullOrEmpty(Name)
                ? Name
                : Lines.FirstOrDefault(l => !string.IsNullOrEmpty(l.UOdooDeliveryId))?.UOdooDeliveryId;
}

public class SapSalesOrderLineRequest
{
    /// <summary>SAP item code.</summary>
    [Required]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>Quantity ordered.</summary>
    [Range(0.0001, double.MaxValue)]
    public double Quantity { get; set; }

    /// <summary>Unit price (required — used as the line selling price in SAP B1).</summary>
    [Required]
    public double UnitPrice { get; set; }

    /// <summary>
    /// Alias for <see cref="UnitPrice"/>. Accepts incoming JSON field <c>price</c> from Odoo/ICC
    /// (Option B pricing semantics). Setting this property sets <see cref="UnitPrice"/>.
    /// </summary>
    [JsonPropertyName("price")]
    public double Price { get => UnitPrice; set => UnitPrice = value; }

    /// <summary>SAP warehouse code for this line.</summary>
    public string? WarehouseCode { get; set; }

    /// <summary>
    /// Odoo sale.order.line ID. Maps to SAP B1 line UDF <c>U_Odoo_SOLine_ID</c>.
    /// </summary>
    public string? UOdooSoLineId { get; set; }

    /// <summary>
    /// Odoo stock.move ID for delivery traceability. Maps to SAP B1 line UDF <c>U_Odoo_Move_ID</c>.
    /// Optional.
    /// </summary>
    public string? UOdooMoveId { get; set; }

    /// <summary>
    /// Odoo delivery (stock.picking) ID. Maps to SAP B1 line UDF <c>U_Odoo_Delivery_ID</c>
    /// if that UDF is defined in your SAP B1 system. Optional.
    /// </summary>
    public string? UOdooDeliveryId { get; set; }
}
