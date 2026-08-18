namespace SapOdooMiddleware.Models.Inventory;

// ── Default-bin seeding job (spec §10) ───────────────────────────────

/// <summary>
/// One item-warehouse seed candidate: the top stocked non-system bin (by quantity)
/// plus the currently configured default, so the job can skip already-set rows.
/// </summary>
public class DefaultBinSeedRow
{
    public string ItemCode { get; set; } = "";
    public string WhsCode { get; set; } = "";
    /// <summary>The bin to seed as default — the bin holding the most stock.</summary>
    public int BinAbs { get; set; }
    /// <summary>Current OITW.DftBinAbs, or null when unset.</summary>
    public int? CurrentDftBinAbs { get; set; }
}

/// <summary>Dry-run analysis of the seeding job — what a live run would do.</summary>
public class DefaultBinSeedAnalysis
{
    /// <summary>All item-warehouse pairs with stock in a real (non-system) bin.</summary>
    public int TotalPairs { get; set; }
    public int TotalItems { get; set; }
    /// <summary>Pairs whose default is empty and would be set.</summary>
    public int PairsToSet { get; set; }
    /// <summary>Pairs already defaulted to the top bin — nothing to do.</summary>
    public int PairsAlreadyCorrect { get; set; }
    /// <summary>Pairs defaulted to a DIFFERENT bin — only touched with overwrite=true.</summary>
    public int PairsWithDifferentDefault { get; set; }
    /// <summary>First rows that a live run would write, for eyeballing before the spike.</summary>
    public List<DefaultBinSeedRow> Sample { get; set; } = new();
}
