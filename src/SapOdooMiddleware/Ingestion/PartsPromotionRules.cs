using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// Decides whether an extracted spare-parts line is promotional / free-of-charge. The vision
/// prompt always emits is_promotional=false and leaves the decision to us (mirrors the Lubes
/// split). Phase A only stores the flag (used by Phase B); the rule errs toward the obvious
/// zero-value give-aways (mugs, hoodies, posters, samples) without guessing from descriptions.
/// </summary>
public static class PartsPromotionRules
{
    public static bool IsPromotional(PartsInvoiceLine line)
    {
        // 100% discount.
        if (line.DiscountPct is >= 100m) return true;

        var qty = line.Quantity ?? 0m;

        // Free goods: a real ordered quantity at zero unit price or zero line total.
        if (qty > 0m && (line.UnitPriceForeign is 0m || line.LineTotalForeign is 0m))
            return true;

        return false;
    }
}
