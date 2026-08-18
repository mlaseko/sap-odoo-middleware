namespace SapOdooMiddleware.Models.Inventory;

// ── Open PO lines (receiving screen) ─────────────────────────────────

/// <summary>Open purchase order line awaiting receipt (OPOR/POR1 + OITM).</summary>
public class OpenPurchaseOrderLine
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string DocDate { get; set; } = "";
    public string CardCode { get; set; } = "";
    public string? CardName { get; set; }
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = "";
    public string? ItemName { get; set; }
    public string? ArticleNumber { get; set; }
    public string? Manufacturer { get; set; }
    public double Quantity { get; set; }
    public double OpenQty { get; set; }
    /// <summary>Warehouse on the PO line — the default receiving destination.</summary>
    public string WhsCode { get; set; } = "";
}

// ── GRPO (OPDN, copy from PO) ────────────────────────────────────────

/// <summary>
/// POST /api/autohub/inv/grpo body. Goods Receipt PO (OPDN, object 20) drawn from
/// open purchase order lines — base refs update the PO's open quantities and vendor
/// liability. Partial receipts allowed; SAP closes fully received PO lines.
/// </summary>
public class GrpoCreate
{
    /// <summary>App-generated GUID for idempotency (written to U_AppRef, max 40 chars).</summary>
    public string AppRef { get; set; } = "";
    /// <summary>Vendor CardCode — must match the base purchase orders.</summary>
    public string CardCode { get; set; } = "";
    public DateTime? DocDate { get; set; }
    public string? Comments { get; set; }
    public List<GrpoLineCreate> Lines { get; set; } = new();
}

public class GrpoLineCreate
{
    /// <summary>Base purchase order DocEntry (OPOR).</summary>
    public int PoDocEntry { get; set; }
    /// <summary>Base purchase order LineNum (POR1).</summary>
    public int PoLineNum { get; set; }
    /// <summary>Quantity received now (≤ the PO line's open quantity).</summary>
    public double Quantity { get; set; }
    /// <summary>Receiving warehouse override; defaults to the PO line's warehouse.</summary>
    public string? WhsCode { get; set; }
    /// <summary>Destination bin (OBIN AbsEntry). Required when the receiving warehouse
    /// is bin-managed and the resolver cannot auto-select.</summary>
    public int? BinAbs { get; set; }
}
