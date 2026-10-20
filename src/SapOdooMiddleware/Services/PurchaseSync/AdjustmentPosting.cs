using SapOdooMiddleware.Models.PurchaseSync;

namespace SapOdooMiddleware.Services.PurchaseSync;

/// <summary>Amounts of one journal line: local currency and, for a foreign document, document currency.</summary>
public sealed record JournalLineAmounts(double DebitLc, double CreditLc, double DebitFc, double CreditFc);

/// <summary>The two lines of a supplier adjustment journal entry (spec v4 Flow D).</summary>
public sealed record AdjustmentPostingLines(
    JournalLineAmounts BusinessPartner,
    JournalLineAmounts GlAccount,
    bool IsForeign);

/// <summary>
/// Pure arithmetic of the adjustment journal entry, kept out of the DI API code so it is unit-tested:
/// <c>credit_supplier</c> credits the business partner and debits the G/L (bank charges: the supplier received
/// less than the top-up); <c>debit_supplier</c> is the mirror. Amounts are positive; for a foreign-currency
/// document the FC amount is the request amount and the LC amount is amount × rate.
/// </summary>
public static class AdjustmentPosting
{
    public const string CreditSupplier = "credit_supplier";
    public const string DebitSupplier = "debit_supplier";

    public static bool IsValidDirection(string? direction) =>
        direction is CreditSupplier or DebitSupplier;

    public static AdjustmentPostingLines Compute(SupplierAdjustmentRequest request, string localCurrency)
    {
        if (!IsValidDirection(request.Direction))
            throw new ArgumentException($"direction must be '{CreditSupplier}' or '{DebitSupplier}', got '{request.Direction}'.");
        if (request.Amount <= 0)
            throw new ArgumentException("amount must be greater than zero.");

        bool foreign = !string.Equals(request.Currency?.Trim(), localCurrency?.Trim(), StringComparison.OrdinalIgnoreCase);
        if (foreign && request.Rate <= 0)
            throw new ArgumentException($"rate must be greater than zero for a {request.Currency} document.");

        double fc = Math.Round(request.Amount, 2, MidpointRounding.AwayFromZero);
        double lc = foreign
            ? Math.Round(request.Amount * request.Rate, 2, MidpointRounding.AwayFromZero)
            : fc;

        var debit = new JournalLineAmounts(DebitLc: lc, CreditLc: 0, DebitFc: foreign ? fc : 0, CreditFc: 0);
        var credit = new JournalLineAmounts(DebitLc: 0, CreditLc: lc, DebitFc: 0, CreditFc: foreign ? fc : 0);

        return request.Direction == CreditSupplier
            ? new AdjustmentPostingLines(BusinessPartner: credit, GlAccount: debit, IsForeign: foreign)
            : new AdjustmentPostingLines(BusinessPartner: debit, GlAccount: credit, IsForeign: foreign);
    }
}
