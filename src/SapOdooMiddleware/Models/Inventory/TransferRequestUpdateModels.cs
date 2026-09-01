namespace SapOdooMiddleware.Models.Inventory;

// ── Transfer Request update (OWTQ, still open) ───────────────────────

/// <summary>
/// PATCH /api/autohub/inv/transfer-requests/{docEntry} body. Quantities are
/// absolute per line (the new total, never a delta), which makes a retried
/// update a natural no-op. Lines cannot be removed from a posted document:
/// reduce a line to its already-fulfilled quantity to close it, or use the
/// close endpoint to cancel every remaining open quantity.
/// </summary>
public class TransferRequestUpdate
{
    /// <summary>Replace the document comments; null leaves them unchanged.</summary>
    public string? Comments { get; set; }

    /// <summary>Absolute quantity updates for existing lines.</summary>
    public List<TransferRequestLineQuantityUpdate> Lines { get; set; } = new();

    /// <summary>New lines appended to the request (same route as the header).</summary>
    public List<TransferRequestLineCreate> AddLines { get; set; } = new();
}

public class TransferRequestLineQuantityUpdate
{
    public int LineNum { get; set; }
    /// <summary>The new total quantity for the line (not a delta).</summary>
    public double Quantity { get; set; }
}

// ── Snapshot (validation input + endpoint response) ──────────────────

/// <summary>Current OWTQ/WTQ1 state, straight from SQL.</summary>
public class TransferRequestSnapshot
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string DocDate { get; set; } = "";
    public string FromWhs { get; set; } = "";
    public string ToWhs { get; set; } = "";
    /// <summary>SAP DocStatus: O = open, C = closed.</summary>
    public string DocStatus { get; set; } = "";
    /// <summary>SAP Canceled flag: Y when the document was cancelled.</summary>
    public string Canceled { get; set; } = "";
    public string? Comments { get; set; }
    public List<TransferRequestSnapshotLine> Lines { get; set; } = new();
}

public class TransferRequestSnapshotLine
{
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = "";
    public string? ItemName { get; set; }
    public double Quantity { get; set; }
    public double OpenQty { get; set; }
    /// <summary>SAP LineStatus: O = open, C = closed.</summary>
    public string LineStatus { get; set; } = "";
}
