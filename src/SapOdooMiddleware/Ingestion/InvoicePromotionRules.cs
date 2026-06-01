using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// Heuristics for flagging an invoice line as promotional (free goods / give-aways) so it is
/// excluded from item creation. A line is promotional if ANY of:
///  - discount is 100% or more,
///  - it has a quantity but zero unit price AND zero line total (free goods),
///  - the pack size is "1 Stk" or the unit is "Stk" (Liqui Moly promo/sample convention).
/// </summary>
public static class InvoicePromotionRules
{
    public static bool IsPromotional(InvoiceLine line)
    {
        if ((line.DiscountPct ?? 0m) >= 100m) return true;
        if ((line.UnitPrice ?? 0m) == 0m && (line.LineTotal ?? 0m) == 0m && line.Quantity is > 0m) return true;
        if (string.Equals(line.PackSize?.Trim(), "1 Stk", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(line.Unit?.Trim(), "Stk", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
