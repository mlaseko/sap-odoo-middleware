using System.ComponentModel.DataAnnotations;

namespace SapOdooMiddleware.Models.Odoo;

/// <summary>
/// Header-only delivery update sent from SAP B1 to confirm delivery in Odoo.
/// </summary>
public class DeliveryUpdateRequest
{
    /// <summary>The Odoo sale.order reference (e.g. "SO0042").</summary>
    [Required]
    public string OdooSoRef { get; set; } = string.Empty;

    /// <summary>SAP Delivery Note number.</summary>
    [Required]
    public string SapDeliveryNo { get; set; } = string.Empty;

    /// <summary>Delivery date (ISO-8601).</summary>
    public DateTime? DeliveryDate { get; set; }

    /// <summary>Delivery status (e.g. "delivered").</summary>
    public string Status { get; set; } = "delivered";
}
