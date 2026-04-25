namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response returned by GET /api/inventory/valuation/total.
/// Represents the total inventory value computed from SAP B1 via the DI API.
/// </summary>
public class InventoryValuationTotalResponse
{
    /// <summary>Currency of the valuation (always "TZS").</summary>
    public string Currency { get; set; } = "TZS";

    /// <summary>The date the valuation was computed as-of (ISO 8601 date, e.g. "2026-04-25").</summary>
    public DateOnly AsOfDate { get; set; }

    /// <summary>Total inventory value in TZS, computed from on-hand quantities and purchase prices.</summary>
    public decimal TotalInventoryValueTzs { get; set; }
}
