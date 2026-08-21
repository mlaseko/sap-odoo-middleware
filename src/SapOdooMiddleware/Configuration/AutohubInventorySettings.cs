namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Autohub inventory app settings — numbering series per document type (spec §6.4).
/// Series are set explicitly on every posted document so future series additions in
/// SAP never silently change which series the app uses. Defaults match the verified
/// production configuration (August 2026 discovery).
/// </summary>
public class AutohubInventorySettings
{
    public const string SectionName = "AutohubInventory";

    /// <summary>Series for Inventory Transfer Requests (object 1250000001, OWTQ).</summary>
    public int TransferRequestSeries { get; set; } = 46;

    /// <summary>Series for Inventory Transfers (object 67, OWTR). "New TR" 74 is the
    /// series in daily use (Primary 21 is legacy).</summary>
    public int TransferSeries { get; set; } = 74;

    /// <summary>Series for Goods Receipts (object 59, OIGN). Phase 4.</summary>
    public int GoodsReceiptSeries { get; set; } = 19;

    /// <summary>Series for Inventory Countings (object 1470000065, OINC). Phase 3.</summary>
    public int CountingSeries { get; set; } = 51;

    /// <summary>Series for Inventory Postings (object 10000071, OIQR). Phase 3.</summary>
    public int PostingSeries { get; set; } = 38;

    /// <summary>Series for Goods Receipt POs (object 20, OPDN). 0 = let SAP use the
    /// default series (discovery found a single series for all objects except transfers).</summary>
    public int GrpoSeries { get; set; } = 0;

    /// <summary>Series for sales Return Requests (ORRR). 0 = SAP default series.</summary>
    public int SalesReturnRequestSeries { get; set; } = 0;

    /// <summary>Series for Goods Returns (object 16, ORDN). 0 = SAP default series.</summary>
    public int GoodsReturnSeries { get; set; } = 0;
}
