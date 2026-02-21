using System.ComponentModel.DataAnnotations;

namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Request payload sent from Odoo to create a Sales Order in SAP B1.
/// </summary>
public class SapSalesOrderRequest
{
    /// <summary>Odoo sale.order reference (e.g. "SO0042") stored on the SAP SO for traceability.</summary>
    [Required]
    public string OdooSoRef { get; set; } = string.Empty;

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
}

public class SapSalesOrderLineRequest
{
    /// <summary>SAP item code.</summary>
    [Required]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>Quantity ordered.</summary>
    [Range(0.0001, double.MaxValue)]
    public double Quantity { get; set; }

    /// <summary>Unit price (optional — can fall back to SAP price list).</summary>
    public double? UnitPrice { get; set; }

    /// <summary>SAP warehouse code for this line.</summary>
    public string? WarehouseCode { get; set; }

    /// <summary>Odoo sale.order.line reference for traceability.</summary>
    public string? OdooLineRef { get; set; }
}
