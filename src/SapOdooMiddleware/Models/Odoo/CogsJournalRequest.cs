using System.ComponentModel.DataAnnotations;

namespace SapOdooMiddleware.Models.Odoo;

/// <summary>
/// Request payload for creating a COGS journal entry in Odoo.
/// Sent by the middleware after fetching cost data from SAP for a posted AR Invoice.
/// </summary>
public class CogsJournalRequest
{
    /// <summary>SAP AR Invoice DocEntry (OINV.DocEntry). Used to locate the Odoo invoice.</summary>
    [Required]
    public int DocEntry { get; set; }

    /// <summary>SAP AR Invoice DocNum (user-facing number). Optional, used in JE ref.</summary>
    public int? DocNum { get; set; }

    /// <summary>SAP Invoice posting date (ISO-8601). Falls back to Odoo invoice date if not provided.</summary>
    public DateTime? DocDate { get; set; }

    /// <summary>
    /// Line-level cost data from SAP INV1.
    /// Each entry provides the item cost for one invoice line.
    /// </summary>
    [Required]
    [MinLength(1)]
    public List<CogsJournalLineRequest> Lines { get; set; } = [];
}

/// <summary>
/// Per-line cost data from SAP for COGS journal entry creation.
/// </summary>
public class CogsJournalLineRequest
{
    /// <summary>
    /// SAP invoice line number (INV1.LineNum). Used for best-match line mapping.
    /// May be null if SAP does not provide it yet (fallback matching used).
    /// </summary>
    public int? LineNum { get; set; }

    /// <summary>SAP item code (INV1.ItemCode). Used for fallback line matching.</summary>
    [Required]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>Invoiced quantity (INV1.Quantity). Used for fallback matching and COGS calculation.</summary>
    [Required]
    [Range(0.0001, double.MaxValue)]
    public double Quantity { get; set; }

    /// <summary>
    /// Unit cost from SAP (INV1.GrossBuyPr). If provided, LineCOGS = UnitCost × Quantity.
    /// Mutually exclusive with <see cref="StockSum"/> — provide one or the other.
    /// </summary>
    public double? UnitCost { get; set; }

    /// <summary>
    /// Total stock value for this line (INV1.StockValue). If provided, LineCOGS = StockSum.
    /// Mutually exclusive with <see cref="UnitCost"/> — provide one or the other.
    /// </summary>
    public double? StockSum { get; set; }
}
