namespace SapOdooMiddleware.Models.Inventory;

// ── Transfer Request (OWTQ) ──────────────────────────────────────────

/// <summary>
/// POST /api/autohub/inv/transfer-requests body. Intent only — no stock movement,
/// no bins (spec §5.2 step 1).
/// </summary>
public class TransferRequestCreate
{
    /// <summary>App-generated GUID for idempotency (written to U_AppRef, max 40 chars).</summary>
    public string AppRef { get; set; } = "";
    public string FromWhs { get; set; } = "";
    public string ToWhs { get; set; } = "";
    /// <summary>Optional posting date (defaults to today).</summary>
    public DateTime? DocDate { get; set; }
    public string? Comments { get; set; }
    public List<TransferRequestLineCreate> Lines { get; set; } = new();
}

public class TransferRequestLineCreate
{
    public string ItemCode { get; set; } = "";
    public double Quantity { get; set; }
}

// ── Inventory Transfer (OWTR) ────────────────────────────────────────

/// <summary>
/// POST /api/autohub/inv/transfers body. Lines drawn from a transfer request carry
/// base refs so SAP closes the request's open quantities (spec §4). Same-warehouse
/// putaway (FromWhs == ToWhs) is allowed — both bins required then.
/// </summary>
public class TransferCreate
{
    /// <summary>App-generated GUID for idempotency (written to U_AppRef, max 40 chars).</summary>
    public string AppRef { get; set; } = "";
    public string FromWhs { get; set; } = "";
    public string ToWhs { get; set; } = "";
    public DateTime? DocDate { get; set; }
    public string? Comments { get; set; }
    public List<TransferLineCreate> Lines { get; set; } = new();
}

public class TransferLineCreate
{
    public string ItemCode { get; set; } = "";
    public double Quantity { get; set; }

    /// <summary>OWTQ DocEntry when this line fulfills a transfer request line.</summary>
    public int? BaseRequestEntry { get; set; }
    /// <summary>WTQ1 LineNum paired with <see cref="BaseRequestEntry"/>.</summary>
    public int? BaseRequestLine { get; set; }

    /// <summary>Source bin (OBIN AbsEntry). Required when the from-warehouse is bin-managed
    /// and the resolver cannot auto-select.</summary>
    public int? FromBinAbs { get; set; }
    /// <summary>Destination bin (OBIN AbsEntry). Required when the to-warehouse is bin-managed
    /// and the resolver cannot auto-select.</summary>
    public int? ToBinAbs { get; set; }
}

// ── Shared result ────────────────────────────────────────────────────

/// <summary>Result of an inventory document post (or an idempotency hit).</summary>
public class InventoryDocResult
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    /// <summary>True when a document with this AppRef already existed — no new post was made.</summary>
    public bool AlreadyExisted { get; set; }
}
