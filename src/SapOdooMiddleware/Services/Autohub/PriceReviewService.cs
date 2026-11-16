using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>A warning on a line's price. Flags never block; they tell the reviewer where to look.</summary>
public sealed record PriceReviewFlag(string Code, string Text);

/// <summary>The reviewer's saved decision on a line, with whether it still matches the current figures.</summary>
public sealed record PriceReviewDecisionView(
    string Status, decimal? FinalPl03, decimal? FinalPl05, decimal? FinalMargin, string? Reason,
    string ReviewedBy, DateTime ReviewedAt, bool Stale, IReadOnlyList<string> StaleReasons);

/// <summary>
/// One invoice line in the price review: what the engine would create it at, why, and the warnings.
/// All money in TZS except the invoice price (document currency). Margin = (PL03 − PL01) ÷ PL03.
/// Error is set when the line cannot be priced; the price fields are then null.
/// </summary>
public sealed record PriceReviewLine(
    Guid LineId, int LineNumber, string? ArticleNumber, string? Description, string? Brand, decimal? Quantity,
    decimal? UnitPriceForeign, decimal? DiscountPct, decimal? EffectiveUnitPrice,
    decimal? CifTzs, decimal? Pl01, decimal? FormulaPl03, decimal? FormulaPl05, decimal? FormulaMargin,
    decimal? Ratio, string? RatioBrand, bool FloorApplied, decimal? Floor, int? RetailRoundingStep,
    IReadOnlyList<PriceReviewFlag> Flags, string? Error, PriceReviewDecisionView? Decision);

/// <summary>Counts for the review header. Unreviewed includes lines whose decision went stale.</summary>
public sealed record PriceReviewSummary(int Total, int Approved, int Held, int Stale, int Unreviewed, int Flagged, int Errors);

/// <summary>The price review for one Autohub invoice document.</summary>
public sealed record PriceReviewDocument(
    Guid DocumentId, string? Currency, decimal? ForexRate, string? ForexError,
    PriceReviewSummary Summary, IReadOnlyList<PriceReviewLine> Lines);

public interface IPriceReviewService
{
    /// <summary>
    /// The price review for every line Bulk Create would create (ReviewStatus create_new / create_failed),
    /// priced exactly as Bulk Create prices it. Null when the document does not exist. Writes nothing.
    /// </summary>
    Task<PriceReviewDocument?> GetReviewAsync(Guid documentId, CancellationToken ct);
}

/// <summary>
/// Builds the read-only price review for an Autohub invoice (price review step 1). Uses the same cost
/// path as <see cref="PartsItemProvisioningService"/> — invoice unit price less the line discount,
/// times the document currency's forex_rate, then the batch pricing engine — so the prices shown are
/// the prices Bulk Create would write. Saved decisions from parts_price_review are attached and marked
/// stale when the figures they were approved on have changed.
/// </summary>
public sealed class PriceReviewService : IPriceReviewService
{
    /// <summary>The statuses Bulk Create creates (PartsReviewRepository.ListCreateNewAsync).</summary>
    private static readonly HashSet<string> CreatableStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "create_new", "create_failed" };

    private readonly IStagingPartsDocumentRepository _docs;
    private readonly IPartsReviewRepository _lines;
    private readonly IPartsPriceReviewRepository _decisions;
    private readonly IForexConversionService _forex;
    private readonly IPricingCalculationService _pricing;
    private readonly AutohubPricingSettings _settings;

    public PriceReviewService(
        IStagingPartsDocumentRepository docs, IPartsReviewRepository lines,
        IPartsPriceReviewRepository decisions, IForexConversionService forex,
        IPricingCalculationService pricing, IOptions<AutohubPricingSettings> settings)
    {
        _docs = docs;
        _lines = lines;
        _decisions = decisions;
        _forex = forex;
        _pricing = pricing;
        _settings = settings.Value;
    }

    public async Task<PriceReviewDocument?> GetReviewAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return null;

        var currency = string.IsNullOrWhiteSpace(doc.Currency) ? null : doc.Currency.Trim().ToUpperInvariant();
        var lines = (await _lines.ListByDocumentAsync(documentId, ct))
            .Where(l => CreatableStatuses.Contains(l.ReviewStatus))
            .OrderBy(l => l.LineNumber)
            .ToList();
        var decisions = await _decisions.GetByDocumentAsync(documentId, ct);

        // One rate for the whole document (same currency, same day), as Bulk Create uses.
        decimal? rate = null;
        string? forexError = null;
        if (currency is null)
            forexError = "Document currency is unknown; cannot convert cost.";
        else
        {
            try { rate = await _forex.GetRateAsync(currency, DateTime.UtcNow, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { forexError = ex.Message; }
        }

        // Cost per line, then price every priceable line in one batch.
        var costs = lines.Select(l => CostOf(l, rate)).ToList();
        var priceable = costs.Select((c, i) => (c, i)).Where(x => x.c.Cif is not null).ToList();
        var quotes = new PricingBatchResult?[lines.Count];
        if (priceable.Count > 0)
        {
            var batch = await _pricing.CalculateBatchDetailedAsync(
                priceable.Select(x => (x.c.Cif!.Value, lines[x.i].Brand)).ToList(), ct);
            for (int k = 0; k < priceable.Count; k++)
                quotes[priceable[k].i] = batch[k];
        }

        var result = new List<PriceReviewLine>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var cost = costs[i];
            var quote = quotes[i]?.Quote;
            var error = cost.Error;
            if (error is null && cost.Cif is null)
                error = forexError ?? "Line cannot be priced.";
            if (error is null && quote is null)
                error = quotes[i]?.Error ?? "Pricing returned no result.";

            decisions.TryGetValue(line.Id, out var saved);
            var decision = saved is null ? null : ViewOf(saved, line, currency, rate, cost.Cif, quote);

            // Flags describe the price that would be written: the reviewer's PL03 when a current
            // decision sets one, otherwise the engine's.
            var flags = quote is null
                ? (IReadOnlyList<PriceReviewFlag>)Array.Empty<PriceReviewFlag>()
                : FlagsFor(quote, decision is { Stale: false, FinalPl03: { } f } ? f : quote.Retail, line.Brand);

            result.Add(new PriceReviewLine(
                LineId: line.Id, LineNumber: line.LineNumber, ArticleNumber: line.SupplierArticleNumber,
                Description: line.Description, Brand: line.Brand, Quantity: line.Quantity,
                UnitPriceForeign: line.UnitPriceForeign, DiscountPct: line.DiscountPct,
                EffectiveUnitPrice: cost.EffectiveUnit,
                CifTzs: cost.Cif is { } c ? Round2(c) : null,
                Pl01: quote?.Cost, FormulaPl03: quote?.Retail, FormulaPl05: quote?.Wholesale,
                FormulaMargin: quote is null ? null : MarginOf(quote.Cost, quote.Retail),
                Ratio: quote?.Ratio, RatioBrand: quote?.RatioBrand,
                FloorApplied: quote?.FloorApplied ?? false, Floor: quote?.Floor,
                RetailRoundingStep: quote?.RetailRoundingStep,
                Flags: flags, Error: error, Decision: decision));
        }

        return new PriceReviewDocument(documentId, currency, rate, forexError, Summarise(result), result);
    }

    // ── Cost (mirrors PartsItemProvisioningService) ────────────────────────────

    private sealed record LineCost(decimal? EffectiveUnit, decimal? Cif, string? Error);

    private static LineCost CostOf(PartsReviewLineRow line, decimal? rate)
    {
        if (line.UnitPriceForeign is not > 0m)
            return new LineCost(null, null, "Line has no positive unit price.");
        var unit = EffectiveUnit(line.UnitPriceForeign.Value, line.DiscountPct);
        return rate is { } r ? new LineCost(unit, unit * r, null) : new LineCost(unit, null, null);
    }

    /// <summary>The invoice unit price less the line discount — the same rule Bulk Create applies.</summary>
    internal static decimal EffectiveUnit(decimal unitPrice, decimal? discountPct) =>
        discountPct is > 0m and < 100m
            ? Math.Round(unitPrice * (1m - discountPct.Value / 100m), 6, MidpointRounding.AwayFromZero)
            : unitPrice;

    // ── Flags ───────────────────────────────────────────────────────────────────

    internal IReadOnlyList<PriceReviewFlag> FlagsFor(PricingQuote quote, decimal pl03, string? brand)
    {
        var flags = new List<PriceReviewFlag>();
        var margin = MarginOf(quote.Cost, pl03);

        if (pl03 <= quote.Cost)
            flags.Add(new("below_cost", "PL03 is at or below the landed cost (PL01)."));
        else if (margin is { } m && m < _settings.PriceReviewMarginLow)
            flags.Add(new("margin_low", $"Margin {m:P0} is below {_settings.PriceReviewMarginLow:P0}."));
        if (margin is { } mh && mh > _settings.PriceReviewMarginHigh)
            flags.Add(new("margin_high", $"Margin {mh:P0} is above {_settings.PriceReviewMarginHigh:P0}."));
        if (quote.FloorApplied)
            flags.Add(new("floor_bound", $"The brand's minimum price ({quote.Floor:N0}) set PL03, not the ratio."));
        if (string.Equals(quote.RatioBrand, "DEFAULT", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(brand)
            && !string.Equals(brand.Trim(), "DEFAULT", StringComparison.OrdinalIgnoreCase))
            flags.Add(new("no_brand_ratio", $"{brand.Trim()} has no ratio of its own; the DEFAULT ratio was used."));
        return flags;
    }

    internal static decimal? MarginOf(decimal pl01, decimal pl03) =>
        pl03 > 0m ? Math.Round((pl03 - pl01) / pl03, 4, MidpointRounding.AwayFromZero) : null;

    // ── Saved decisions ─────────────────────────────────────────────────────────

    /// <summary>
    /// The decision as shown, marked stale when any figure it was based on has changed. A stale
    /// decision must be approved again before its line can be created.
    /// </summary>
    internal static PriceReviewDecisionView ViewOf(
        PartsPriceDecision saved, PartsReviewLineRow line, string? currency, decimal? rate,
        decimal? cif, PricingQuote? quote)
    {
        var reasons = new List<string>();
        if (!SameMoney(saved.UnitPriceForeign, line.UnitPriceForeign, 4)) reasons.Add("Invoice unit price changed.");
        if (!SameMoney(saved.DiscountPct, line.DiscountPct, 4)) reasons.Add("Line discount changed.");
        if (!string.Equals(saved.Currency ?? "", currency ?? "", StringComparison.OrdinalIgnoreCase))
            reasons.Add("Document currency changed.");
        if (!SameMoney(saved.ForexRate, rate, 6)) reasons.Add("Exchange rate changed.");
        if (!SameMoney(saved.CifTzs, cif, 2)) reasons.Add("Cost (CIF) changed.");
        if (!SameMoney(saved.FormulaPl03, quote?.Retail, 2)) reasons.Add("Calculated PL03 changed (pricing settings).");

        var finalMargin = saved.FinalPl03 is { } f && saved.Pl01 is { } c ? MarginOf(c, f) : null;
        return new PriceReviewDecisionView(
            saved.Status, saved.FinalPl03, saved.FinalPl05, finalMargin, saved.Reason,
            saved.ReviewedBy, saved.ReviewedAt, reasons.Count > 0, reasons);
    }

    private static bool SameMoney(decimal? a, decimal? b, int decimals) =>
        (a, b) switch
        {
            (null, null) => true,
            ({ } x, { } y) => Math.Round(x, decimals, MidpointRounding.AwayFromZero)
                              == Math.Round(y, decimals, MidpointRounding.AwayFromZero),
            _ => false,
        };

    private static PriceReviewSummary Summarise(IReadOnlyList<PriceReviewLine> lines)
    {
        int approved = 0, held = 0, stale = 0, unreviewed = 0;
        foreach (var l in lines)
        {
            if (l.Decision is null) { unreviewed++; continue; }
            if (l.Decision.Stale) { stale++; unreviewed++; continue; }
            if (l.Decision.Status == "approved") approved++;
            else if (l.Decision.Status == "held") held++;
        }
        return new PriceReviewSummary(
            Total: lines.Count, Approved: approved, Held: held, Stale: stale, Unreviewed: unreviewed,
            Flagged: lines.Count(l => l.Flags.Count > 0), Errors: lines.Count(l => l.Error is not null));
    }

    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
