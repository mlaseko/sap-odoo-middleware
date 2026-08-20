namespace SapOdooMiddleware.Models.Inventory;

// ── Counting session creation (OINC) ─────────────────────────────────

/// <summary>
/// POST /api/autohub/inv/countings body. Scope = warehouse + bin range (or explicit
/// bin list) for bin warehouses; just the warehouse for non-bin warehouse 01.
/// The middleware generates one line per item-per-bin from current stock (spec §8.3)
/// with the system quantity snapshotted by SAP at creation.
/// </summary>
public class CountingCreate
{
    /// <summary>App-generated GUID for idempotency (written to U_AppRef, max 40 chars).</summary>
    public string AppRef { get; set; } = "";
    public string WhsCode { get; set; } = "";
    /// <summary>Inclusive BinCode range start (bin warehouses; alternative to <see cref="BinAbsList"/>).</summary>
    public string? BinFrom { get; set; }
    /// <summary>Inclusive BinCode range end.</summary>
    public string? BinTo { get; set; }
    /// <summary>Explicit OBIN AbsEntry list (alternative to the BinFrom/BinTo range).</summary>
    public List<int>? BinAbsList { get; set; }
    /// <summary>
    /// Optional item filter: count only these items. In a bin warehouse this may be
    /// used INSTEAD of a bin scope (one line per stocked bin of each item across the
    /// whole warehouse) or combined with one (intersection).
    /// </summary>
    public List<string>? ItemCodes { get; set; }
    /// <summary>Count date (defaults to today).</summary>
    public DateTime? CountDate { get; set; }
    /// <summary>
    /// OPTIONAL cross-check only — the SAP branch (Business Place) id the caller
    /// believes applies. The middleware always resolves the real branch from
    /// <see cref="WhsCode"/> (OWHS.BPLid) as the source of truth; when this field is
    /// supplied and differs from the resolved branch, the request is rejected (422).
    /// Omit it to let the middleware handle branches entirely.
    /// </summary>
    public int? BranchId { get; set; }
}

/// <summary>One generated counting line: item + warehouse (+ bin for bin warehouses).</summary>
public class CountingLineSeed
{
    public string ItemCode { get; set; } = "";
    public string WhsCode { get; set; } = "";
    public int? BinEntry { get; set; }
}

// ── Counting session listing / detail ────────────────────────────────

/// <summary>Counting session header with progress (from OINC + INC1).</summary>
public class CountingSessionSummary
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string CountDate { get; set; } = "";
    /// <summary>"O" open, "C" closed.</summary>
    public string Status { get; set; } = "";
    public string WhsCode { get; set; } = "";
    public int TotalLines { get; set; }
    public int CountedLines { get; set; }
}

/// <summary>One counting line for the count-capture screen (INC1 + OITM + OBIN).</summary>
public class CountingLineDetail
{
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = "";
    public string? ItemName { get; set; }
    public string? ArticleNumber { get; set; }
    public string? Manufacturer { get; set; }
    public string WhsCode { get; set; } = "";
    public int? BinEntry { get; set; }
    public string? BinCode { get; set; }
    /// <summary>System (snapshot) quantity — INC1.InWhsQty.</summary>
    public decimal SystemQty { get; set; }
    /// <summary>Counted quantity — null until captured.</summary>
    public decimal? CountedQty { get; set; }
    public bool Counted { get; set; }
    /// <summary>"O" open, "C" closed (posted).</summary>
    public string LineStatus { get; set; } = "";
}

// ── Count capture (PATCH) ────────────────────────────────────────────

/// <summary>PATCH /api/autohub/inv/countings/{docEntry}/lines body.</summary>
public class CountingUpdateRequest
{
    /// <summary>Counted quantities for existing lines.</summary>
    public List<CountingLineUpdate> Updates { get; set; } = new();
    /// <summary>Unexpected finds — items counted in a bin but not on the generated list
    /// (how misplaced stock is caught, spec §5.3 step 2).</summary>
    public List<CountingLineAddition> Additions { get; set; } = new();
}

public class CountingLineUpdate
{
    public int LineNum { get; set; }
    public double CountedQty { get; set; }
}

public class CountingLineAddition
{
    public string ItemCode { get; set; } = "";
    /// <summary>Bin (OBIN AbsEntry) where the item was found. Null for non-bin warehouse 01.</summary>
    public int? BinAbs { get; set; }
    public double CountedQty { get; set; }
}

// ── Variance review ──────────────────────────────────────────────────

/// <summary>Variance line (spec §8.4): system vs counted vs value, most expensive first.</summary>
public class CountingVarianceLine
{
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = "";
    public string? ItemName { get; set; }
    public string? ArticleNumber { get; set; }
    public string? Manufacturer { get; set; }
    public string WhsCode { get; set; } = "";
    public string? BinCode { get; set; }
    public decimal SystemQty { get; set; }
    public decimal? CountedQty { get; set; }
    public decimal VarianceQty { get; set; }
    /// <summary>VarianceQty × OITW.AvgPrice.</summary>
    public decimal VarianceValue { get; set; }
    public bool Counted { get; set; }
    public string LineStatus { get; set; } = "";
}

// ── Inventory Posting (OIQR) ─────────────────────────────────────────

/// <summary>
/// POST /api/autohub/inv/postings body. Creates the Inventory Posting from
/// reviewer-approved counting lines (base refs close them in SAP). Lines needing a
/// recount are simply left out and stay open.
/// </summary>
public class PostingCreate
{
    /// <summary>App-generated GUID for idempotency (written to U_AppRef, max 40 chars).</summary>
    public string AppRef { get; set; } = "";
    public int CountingDocEntry { get; set; }
    /// <summary>INC1 LineNum values approved for posting. Must all be counted and open.</summary>
    public List<int> LineNums { get; set; } = new();
}

/// <summary>One posting line handed to the DI API (built server-side from the counting lines).</summary>
public class CountingPostLine
{
    public int LineNum { get; set; }
    public double CountedQty { get; set; }
}

// ── Direct Inventory Posting (no counting session) ──────────────────

/// <summary>
/// POST /api/autohub/inv/postings/direct body. Standalone stock correction: an
/// Inventory Posting (OIQR) with NO base counting document. <c>counted_qty</c> is the
/// absolute new quantity — SAP computes the variance against current stock at post
/// time and writes the stock/GL adjustment.
/// </summary>
public class DirectPostingCreate
{
    /// <summary>App-generated GUID for idempotency (written to U_AppRef, max 40 chars).</summary>
    public string AppRef { get; set; } = "";
    public string WhsCode { get; set; } = "";
    public DateTime? DocDate { get; set; }
    /// <summary>Reason for the correction (stored as the document remarks).</summary>
    public string? Remarks { get; set; }
    public List<DirectPostingLineCreate> Lines { get; set; } = new();
}

public class DirectPostingLineCreate
{
    public string ItemCode { get; set; } = "";
    /// <summary>Bin (OBIN AbsEntry) whose count is being set. REQUIRED in bin-managed
    /// warehouses — a correction targets a specific bin deliberately, never a guessed one.</summary>
    public int? BinAbs { get; set; }
    /// <summary>The absolute new quantity (0 zeroes the stock out).</summary>
    public double CountedQty { get; set; }
}
