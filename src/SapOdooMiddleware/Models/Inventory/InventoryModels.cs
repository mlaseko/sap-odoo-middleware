namespace SapOdooMiddleware.Models.Inventory;

// ── Warehouse ────────────────────────────────────────────────────────

public class WarehouseInfo
{
    public string WhsCode { get; set; } = "";
    public string WhsName { get; set; } = "";
    public bool BinActivated { get; set; }
}

// ── Stock / Bin ──────────────────────────────────────────────────────

/// <summary>One bin's stock for a single item (from OIBQ + OBIN + OITM).</summary>
public class BinStockLine
{
    public string ItemCode { get; set; } = "";
    public string? ItemName { get; set; }
    public string? ArticleNumber { get; set; }
    public string? Manufacturer { get; set; }
    public string WhsCode { get; set; } = "";
    public int BinAbs { get; set; }
    public string BinCode { get; set; } = "";
    public decimal OnHandQty { get; set; }
}

/// <summary>Warehouse-level stock (for non-bin warehouse 01).</summary>
public class WarehouseStockLine
{
    public string ItemCode { get; set; } = "";
    public string? ItemName { get; set; }
    public string? ArticleNumber { get; set; }
    public string? Manufacturer { get; set; }
    public string WhsCode { get; set; } = "";
    public decimal OnHand { get; set; }
}

/// <summary>Normalized stock response — works for both bin and non-bin warehouses.</summary>
public class StockResponse
{
    public string ItemCode { get; set; } = "";
    public string? ItemName { get; set; }
    public string? ArticleNumber { get; set; }
    public string? Manufacturer { get; set; }
    public string WhsCode { get; set; } = "";
    public bool BinManaged { get; set; }
    /// <summary>Warehouse-level on-hand (sum across bins, or OITW for non-bin).</summary>
    public decimal TotalOnHand { get; set; }
    /// <summary>Per-bin breakdown. Empty for non-bin warehouses.</summary>
    public List<BinDetail> Bins { get; set; } = new();
}

public class BinDetail
{
    public int BinAbs { get; set; }
    public string BinCode { get; set; } = "";
    public decimal OnHandQty { get; set; }
}

// ── Bin Resolver ─────────────────────────────────────────────────────

public enum BinDirection { Source, Destination }

/// <summary>
/// Bin resolver output. Exactly one of four states:
/// <list type="bullet">
///   <item><c>auto</c> — one bin, auto-selected (89% of cases); see <see cref="Auto"/>.</item>
///   <item><c>options</c> — 2-5 bins, user confirms; see <see cref="Options"/>.</item>
///   <item><c>required</c> — no stock / no default, user must scan.</item>
///   <item><c>not_bin_managed</c> — warehouse has no bins (whs 01); no bin UI at all.</item>
/// </list>
/// </summary>
public class BinResolution
{
    public string Resolution { get; set; } = ""; // "auto" | "options" | "required" | "not_bin_managed"
    public BinOption? Auto { get; set; }
    public List<BinOption>? Options { get; set; }
}

public class BinOption
{
    public int BinAbs { get; set; }
    public string BinCode { get; set; } = "";
    public decimal OnHandQty { get; set; }
}

/// <summary>A warehouse's SAP branch (Business Place) assignment — OWHS.BPLid + OBPL.</summary>
public class WarehouseBranch
{
    public int BplId { get; set; }
    public string? BranchName { get; set; }
    /// <summary>False when OBPL marks the branch disabled.</summary>
    public bool Active { get; set; }
}

/// <summary>One bin of a warehouse (for the free bin-picker list).</summary>
public class WarehouseBin
{
    public int BinAbs { get; set; }
    public string BinCode { get; set; } = "";
    /// <summary>True for the SYSTEM-BIN-LOCATION (usually not a sensible manual pick).</summary>
    public bool SysBin { get; set; }
}

// ── Transfer Request ─────────────────────────────────────────────────

/// <summary>Open transfer request line (from OWTQ/WTQ1 + OITM).</summary>
public class OpenTransferRequestLine
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string DocDate { get; set; } = "";
    public string FromWhs { get; set; } = "";
    public string ToWhs { get; set; } = "";
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = "";
    public string? ItemName { get; set; }
    public string? ArticleNumber { get; set; }
    public string? Manufacturer { get; set; }
    public double Quantity { get; set; }
    public double OpenQty { get; set; }
}
