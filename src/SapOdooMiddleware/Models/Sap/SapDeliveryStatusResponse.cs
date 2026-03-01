namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response returned when querying the document status of a Delivery Note in SAP B1.
/// Used to validate that a Goods Return can be created against the delivery.
/// </summary>
public class SapDeliveryStatusResponse
{
    /// <summary>SAP Delivery Note DocEntry (internal key). Maps to ODLN.DocEntry.</summary>
    public int DocEntry { get; set; }

    /// <summary>SAP Delivery Note DocNum (user-facing number). Maps to ODLN.DocNum.</summary>
    public int DocNum { get; set; }

    /// <summary>
    /// Document status: "open" or "closed".
    /// A delivery is "closed" when it has been fully returned or cancelled.
    /// Goods returns can only be created against open deliveries.
    /// </summary>
    public string Status { get; set; } = string.Empty;
}
