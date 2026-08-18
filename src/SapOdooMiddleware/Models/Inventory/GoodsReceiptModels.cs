namespace SapOdooMiddleware.Models.Inventory;

// ── Goods Receipt (OIGN) ─────────────────────────────────────────────

/// <summary>
/// POST /api/autohub/inv/goods-receipts body. Standalone (non-PO) stock-in per
/// spec §5.1/§9.1. If the business later confirms receipts against purchase orders,
/// that becomes a separate GRPO endpoint — this document does not touch PO open
/// quantities or vendor liability.
/// </summary>
public class GoodsReceiptCreate
{
    /// <summary>App-generated GUID for idempotency (written to U_AppRef, max 40 chars).</summary>
    public string AppRef { get; set; } = "";
    public string WhsCode { get; set; } = "";
    /// <summary>Optional posting date (defaults to today).</summary>
    public DateTime? DocDate { get; set; }
    public string? Comments { get; set; }
    public List<GoodsReceiptLineCreate> Lines { get; set; } = new();
}

public class GoodsReceiptLineCreate
{
    public string ItemCode { get; set; } = "";
    public double Quantity { get; set; }
    /// <summary>Optional unit cost — omitted, SAP uses the item cost (spec §4).</summary>
    public double? UnitCost { get; set; }
    /// <summary>Destination bin (OBIN AbsEntry). Required when the warehouse is
    /// bin-managed and the resolver cannot auto-select.</summary>
    public int? BinAbs { get; set; }
}

// ── Default bin ("save as default bin for this item") ───────────────

/// <summary>
/// PUT /api/autohub/inv/default-bin body. Sets OITW.DftBinAbs so the destination
/// resolver auto-selects this bin next time (resolver rung 3 → rung 1, spec §7/§10).
/// Defaults suggest, never block — DefaultBinEnforced stays off.
/// </summary>
public class DefaultBinUpdate
{
    public string ItemCode { get; set; } = "";
    public string WhsCode { get; set; } = "";
    public int BinAbs { get; set; }
}
