namespace SapOdooMiddleware.Models.Inventory;

// ── Posting at current stock ─────────────────────────────────────────────────
//
// A counting line is a physical count at one moment. Stock keeps moving after it
// (deliveries, returns, transfers), and a session can wait days for approval. With
// every counting posting, each line's count is carried forward to the posting moment:
//   updated count = counted + (stock in − stock out of that bin since it was counted)
//   adjustment    = updated count − stock now
// and the posting measures every line against stock now, so a bin can no longer be
// driven below zero by movements SAP already booked.

/// <summary>When and what the app recorded for one counted line (inv_count_history).</summary>
public class CountingLineCount
{
    public int LineNum { get; set; }
    /// <summary>The count as captured in the app. Used only when it equals the counting's own count.</summary>
    public decimal? CountedQty { get; set; }
    /// <summary>When the line was captured in the app.</summary>
    public DateTimeOffset? CountedAt { get; set; }
    /// <summary>Optional guard: the updated count the reviewer saw. A different value now refuses with 409.</summary>
    public decimal? ExpectedUpdatedQty { get; set; }
}

/// <summary>POST /api/autohub/inv/countings/{docEntry}/recalc body.</summary>
public class CountingRecalcRequest
{
    public List<CountingLineCount> LineCounts { get; set; } = new();
}

/// <summary>One stock movement into or out of a counted bin (OILM + OBTL).</summary>
public class CountingLineMovement
{
    public int LineNum { get; set; }
    /// <summary>SAP object type of the document (15 delivery, 14 credit memo, 67 transfer, ...).</summary>
    public int TransType { get; set; }
    public string? BaseRef { get; set; }
    /// <summary>When the document was entered in SAP (CreateDate + CreateTime, SAP local time).</summary>
    public DateTime CreatedAt { get; set; }
    public decimal QtyIn { get; set; }
    public decimal QtyOut { get; set; }
}

/// <summary>What one counted line becomes when it is posted against current stock.</summary>
public class CountingRecalcLine
{
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = "";
    public string WhsCode { get; set; } = "";
    public int? BinEntry { get; set; }
    public string? BinCode { get; set; }
    /// <summary>INC1.InWhsQty, the stock SAP recorded for the count date.</summary>
    public decimal SystemQty { get; set; }
    /// <summary>The physical count.</summary>
    public decimal CountedQty { get; set; }
    /// <summary>The moment movements are measured from (SAP local time).</summary>
    public DateTime MeasuredFrom { get; set; }
    /// <summary>False when the app has no capture time for the line; the counting's count date is used.</summary>
    public bool CountTimeKnown { get; set; }
    public decimal MovedIn { get; set; }
    public decimal MovedOut { get; set; }
    public decimal NetMovement { get; set; }
    /// <summary>CountedQty + NetMovement.</summary>
    public decimal UpdatedQty { get; set; }
    /// <summary>Stock in the bin (or warehouse, for a line without a bin) now.</summary>
    public decimal StockNow { get; set; }
    /// <summary>UpdatedQty − StockNow; null for a conflict.</summary>
    public decimal? Adjustment { get; set; }
    /// <summary>"post" (adjustment ≠ 0), "zero" (matches stock now) or "conflict" (more left the bin than was counted).</summary>
    public string Status { get; set; } = "";
    /// <summary>Price on the posting price list (PL01, cost); null when the item has none. Preview only.</summary>
    public decimal? UnitPrice { get; set; }
    /// <summary>Movements after the line was counted (carried into UpdatedQty).</summary>
    public List<CountingLineMovement> Movements { get; set; } = new();
    /// <summary>Stock into the bin since the counting's count date (start of day), before or after the count.</summary>
    public decimal MovedInSinceCountDate { get; set; }
    /// <summary>Stock out of the bin since the counting's count date (start of day), before or after the count.</summary>
    public decimal MovedOutSinceCountDate { get; set; }
    /// <summary>
    /// Every movement since the count date, for the reviewer: the ones before
    /// <see cref="MeasuredFrom"/> were already seen by the counter; the ones at or
    /// after it are in <see cref="Movements"/> and carried forward.
    /// </summary>
    public List<CountingLineMovement> MovementsSinceCountDate { get; set; } = new();
    /// <summary>The item's cost SAP values stock going out at: OITW.AvgPrice, else OITM.AvgPrice.</summary>
    public decimal? WarehouseCost { get; set; }
    /// <summary>
    /// The line takes stock out (adjustment &lt; 0) of a moving-average item that has no
    /// cost in SAP (warehouse and item cost 0): SAP refuses it with 10001287 "Item cost
    /// not found". Fixed in SAP with an Inventory Revaluation (price change).
    /// </summary>
    public bool NoWarehouseCost { get; set; }
}

/// <summary>What SAP values a counting line's stock at (OITM.EvalSystem, OITW / OITM AvgPrice).</summary>
public class CountingLineCost
{
    public int LineNum { get; set; }
    /// <summary>A moving average, S standard, F FIFO, B serial/batch.</summary>
    public string EvalSystem { get; set; } = "";
    public decimal WarehouseAvgPrice { get; set; }
    public decimal ItemAvgPrice { get; set; }
}

/// <summary>POST /api/autohub/inv/countings/{docEntry}/recalc response.</summary>
public class CountingRecalcResult
{
    /// <summary>Always true; kept so the app can tell this middleware from an older one.</summary>
    public bool Enabled { get; set; }
    public DateTime? CountDate { get; set; }
    public DateTime? CalculatedAt { get; set; }
    public List<CountingRecalcLine> Lines { get; set; } = new();
}
