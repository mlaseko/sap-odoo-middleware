namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// A single row from the inventory transaction history query.
/// Covers OINM-based inventory movements (invoices, deliveries, returns, goods
/// receipts/issues, inventory transfers, opening balances) plus open Purchase Orders.
/// </summary>
public class TransactionHistoryItem
{
    public string ItemCode { get; set; } = "";
    public string? ItemName { get; set; }
    public string? UItemName { get; set; }
    public string? UItemManufacturer { get; set; }
    public DateTime PostingDate { get; set; }
    public string? DocumentNumber { get; set; }
    public string TransactionType { get; set; } = "";
    public string DocumentStatus { get; set; } = "";
    public string? BpName { get; set; }
    public string? SalesEmployee { get; set; }
    public string? PostedByUser { get; set; }
    public string? Warehouse { get; set; }
    public decimal QtyIn { get; set; }
    public decimal QtyOut { get; set; }
    public decimal NetMovement { get; set; }
    public decimal? OrderedQty { get; set; }
    public decimal? OpenQty { get; set; }
}
