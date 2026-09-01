namespace SapOdooMiddleware.Models.Inventory;

// ── Pick list picking operations (OPKL/PKL1/PKL2) ────────────────────
//
// The Autohub app browses pick lists from its Neon mirror; only the WRITES
// come through the middleware. Quantities are ABSOLUTE (the target picked
// quantity / the full bin breakdown), so replaying an identical request is
// naturally idempotent — SAP ends in the same state.

/// <summary>One requested bin allocation row (absolute quantity from that bin).</summary>
public class PickBinAllocation
{
    public int BinAbs { get; set; }
    public double Quantity { get; set; }
}

/// <summary>
/// PATCH /api/autohub/inv/pick-lists/{absEntry}/allocations body — re-bin one or
/// more RELEASED lines before picking. The allocations replace the line's current
/// released allocation (total per line must stay within the released quantity).
/// </summary>
public class PickListAllocationUpdate
{
    /// <summary>Client idempotency/trace reference (logged; the update itself is absolute).</summary>
    public string AppRef { get; set; } = "";
    /// <summary>Display name of the app user making the change — recorded in the auto-generated Remarks note.</summary>
    public string ChangedBy { get; set; } = "";
    public List<PickListAllocationLine> Lines { get; set; } = new();
}

public class PickListAllocationLine
{
    /// <summary>PKL1.PickEntry of the line being re-binned.</summary>
    public int PickEntry { get; set; }
    public List<PickBinAllocation> Allocations { get; set; } = new();
}

/// <summary>
/// POST /api/autohub/inv/pick-lists/{absEntry}/pick body — set the ABSOLUTE picked
/// quantity per line (with the full bin breakdown for bin-managed warehouses).
/// picked_qty below the total releasable leaves the line Partially Picked; equal
/// marks it Picked. Lines not listed are left untouched.
/// </summary>
public class PickListPickRequest
{
    public string AppRef { get; set; } = "";
    /// <summary>Display name of the app user picking — recorded in the Remarks note when bins change.</summary>
    public string ChangedBy { get; set; } = "";
    public List<PickListPickLine> Lines { get; set; } = new();
}

public class PickListPickLine
{
    public int PickEntry { get; set; }
    /// <summary>Absolute picked quantity for the line (current picked ≤ value ≤ picked + remaining released).</summary>
    public double PickedQty { get; set; }
    /// <summary>Full bin breakdown for <see cref="PickedQty"/>; required for bin-managed warehouses.</summary>
    public List<PickBinAllocation> Allocations { get; set; } = new();
}

// ── Read-side snapshot (SQL, for validation and the response) ────────

public class PickListSnapshot
{
    public int AbsEntry { get; set; }
    public string Status { get; set; } = "";
    public bool Canceled { get; set; }
    public string Remarks { get; set; } = "";
    public List<PickListLineSnapshot> Lines { get; set; } = new();
}

public class PickListLineSnapshot
{
    public int PickEntry { get; set; }
    public int OrderEntry { get; set; }
    public int OrderLine { get; set; }
    public string ItemCode { get; set; } = "";
    public string WhsCode { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>PKL1.RelQtty — quantity still released (remaining to pick).</summary>
    public double ReleasedQty { get; set; }
    /// <summary>PKL1.PickQtty — quantity already picked.</summary>
    public double PickedQty { get; set; }
    public string PickStatus { get; set; } = "";
    public List<PickListBinSnapshot> Allocations { get; set; } = new();
}

public class PickListBinSnapshot
{
    public int BinAbs { get; set; }
    public string BinCode { get; set; } = "";
    /// <summary>PKL2.RelQtty — released (still to pick) from this bin.</summary>
    public double ReleasedQty { get; set; }
    /// <summary>PKL2.PickQtty — already picked from this bin.</summary>
    public double PickedQty { get; set; }
}

// ── Validated write plan (planner → DI API) ──────────────────────────

/// <summary>One line the DI API should write: absolute picked qty + full bin breakdown.</summary>
public class PickListLineWrite
{
    public int PickEntry { get; set; }
    /// <summary>Null for allocation-only updates (released re-binning, no status change).</summary>
    public double? PickedQty { get; set; }
    public List<PickBinAllocation> Allocations { get; set; } = new();
}

// ── Result ───────────────────────────────────────────────────────────

/// <summary>Response for both pick-list write endpoints: the refreshed document state.</summary>
public class PickListActionResult
{
    public int AbsEntry { get; set; }
    public string Status { get; set; } = "";
    /// <summary>True when the request matched SAP's current state and nothing was written.</summary>
    public bool AlreadyApplied { get; set; }
    /// <summary>False when the change posted but the automatic Remarks note could not be written.</summary>
    public bool NoteWritten { get; set; } = true;
    public string Remarks { get; set; } = "";
    public List<PickListLineSnapshot> Lines { get; set; } = new();
}
