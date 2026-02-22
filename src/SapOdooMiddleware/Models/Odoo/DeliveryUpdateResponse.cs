namespace SapOdooMiddleware.Models.Odoo;

/// <summary>
/// Response returned after successfully confirming delivery in Odoo.
/// </summary>
public class DeliveryUpdateResponse
{
    /// <summary>The Odoo sale.order identifier that was updated.</summary>
    public string UOdooSoId { get; set; } = string.Empty;

    /// <summary>Odoo stock.picking id that was validated.</summary>
    public int PickingId { get; set; }

    /// <summary>Odoo stock.picking name (e.g. "WH/OUT/00001").</summary>
    public string PickingName { get; set; } = string.Empty;

    /// <summary>Final state of the picking after validation.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>SAP Delivery Note number written onto the picking.</summary>
    public string SapDeliveryNo { get; set; } = string.Empty;
}
