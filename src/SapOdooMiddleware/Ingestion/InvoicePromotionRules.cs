using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// Heuristics for invoice lines that look like free / bonus goods.
///
/// Two distinct notions, deliberately separated:
///  - <see cref="IsFreeGoods"/> — a line with a quantity but NO unit price AND NO line total. This is
///    genuinely valueless and safe to auto-skip (the auto-match pass skips these).
///  - <see cref="IsPromotional"/> — a softer "give this a second look" hint used only for the review-UI
///    flag (italic row). It additionally covers 100%-discount lines and the "Stk" sample convention.
///
/// Why they differ: a 100%-discount line that STILL carries a unit price is often a real product
/// received as a bonus (free Liqui Moly stock the operator wants in inventory), not throw-away
/// merchandise. Those are only distinguishable from true promo items (banners, caps, racks, t-shirts)
/// by their description, so we no longer auto-skip them — we flag them and let the reviewer decide.
/// </summary>
public static class InvoicePromotionRules
{
    /// <summary>
    /// Truly zero-value free goods: a quantity but no unit price and no line total. Safe to auto-skip.
    /// </summary>
    public static bool IsFreeGoods(decimal? unitPrice, decimal? lineTotal, decimal? quantity) =>
        (unitPrice ?? 0m) == 0m && (lineTotal ?? 0m) == 0m && quantity is > 0m;

    public static bool IsFreeGoods(InvoiceLine line) =>
        IsFreeGoods(line.UnitPrice, line.LineTotal, line.Quantity);

    /// <summary>
    /// Review-UI flag (italic). Broader than <see cref="IsFreeGoods"/>: also covers 100%-discount lines
    /// and the "1 Stk" / "Stk" sample convention. This only affects how the line is highlighted — it is
    /// NOT used to auto-skip, so a flagged line still flows through matching/creation for the operator.
    /// </summary>
    public static bool IsPromotional(InvoiceLine line)
    {
        if (IsFreeGoods(line)) return true;
        if ((line.DiscountPct ?? 0m) >= 100m) return true;
        if (string.Equals(line.PackSize?.Trim(), "1 Stk", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(line.Unit?.Trim(), "Stk", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
