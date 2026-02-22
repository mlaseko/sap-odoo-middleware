using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Models.Odoo;

/// <summary>
/// Header-only delivery update sent from SAP B1 to confirm delivery in Odoo.
/// </summary>
public class DeliveryUpdateRequest
{
    /// <summary>
    /// The Odoo sale.order identifier (e.g. "SO0042") used to locate the sale.order in Odoo.
    /// Maps to JSON field <c>u_odoo_so_id</c>.
    /// </summary>
    [Required]
    public string UOdooSoId { get; set; } = string.Empty;

    /// <summary>
    /// [Deprecated] Use <c>u_odoo_so_id</c> instead.
    /// Accepted for backwards compatibility; ignored when <c>u_odoo_so_id</c> is present.
    /// </summary>
    public string? OdooSoRef { get; set; }

    /// <summary>SAP Delivery Note number.</summary>
    [Required]
    public string SapDeliveryNo { get; set; } = string.Empty;

    /// <summary>Delivery date (ISO-8601).</summary>
    public DateTime? DeliveryDate { get; set; }

    /// <summary>Delivery status (e.g. "delivered").</summary>
    public string Status { get; set; } = "delivered";

    /// <summary>
    /// Returns the effective Odoo SO identifier: <c>UOdooSoId</c> if set,
    /// otherwise falls back to the deprecated <c>OdooSoRef</c>.
    /// </summary>
    [JsonIgnore]
    public string ResolvedSoId =>
        !string.IsNullOrEmpty(UOdooSoId) ? UOdooSoId : (OdooSoRef ?? string.Empty);
}
