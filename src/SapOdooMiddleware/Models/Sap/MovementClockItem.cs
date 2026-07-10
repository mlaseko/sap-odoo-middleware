namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// A single item from the Movement Clock stock-classification query.
/// Classifies spare-parts inventory by sales velocity, recency, and age.
/// </summary>
public class MovementClockItem
{
    public string ItemCode { get; set; } = "";
    public string ItemName { get; set; } = "";
    public decimal CurrentStock { get; set; }
    public decimal AverageCost { get; set; }
    public DateTime ItemCreationDate { get; set; }
    public int ItemAgeDays { get; set; }
    public int DaysSinceLastSale { get; set; }
    public int MonthsWithSalesLast12Mo { get; set; }
    public decimal AvgMonthlyConsumption { get; set; }
    public decimal TotalQtyLast12Mo { get; set; }
    public string MovementClockClassification { get; set; } = "";
    public string RecommendedAction { get; set; } = "";
    public decimal EstimatedHoldingCostTzs { get; set; }
    public int PriorityScore { get; set; }
}
