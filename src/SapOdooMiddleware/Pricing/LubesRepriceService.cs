using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Pricing;

/// <summary>One item in a bulk reprice request (EurCost null = use the stored cost).</summary>
public record RepriceItemInput(string ItemCode, decimal? EurCost);

/// <summary>Preview of one item's reprice: current state, inputs, new prices, deltas.</summary>
public record RepricePreviewLine(
    string ItemCode,
    bool Found,
    string? ItemName,
    string? Error,
    decimal? EurCost,
    string? EurCostSource,
    decimal Rate,
    decimal? CifTzs,
    string? PricingCategory,
    string? CategoryPath,
    Dictionary<int, decimal> OldNet,
    Dictionary<int, decimal>? NewNet,
    /// <summary>New INCL-VAT shelf prices per price list — the exact rounded values
    /// the calculator produced (the business-primary figures).</summary>
    Dictionary<int, decimal>? NewInclVat,
    Dictionary<int, decimal?>? DeltaPct,
    List<string> Warnings,
    PricingTrace? Trace)
{
    public bool CanApply => Found && Error is null && NewNet is not null;
}

/// <summary>Outcome of applying one reprice.</summary>
public record RepriceApplyResult(
    RepricePreviewLine Preview, bool Applied, List<string> OdooNotes, string? Error);

public interface ILubesRepriceService
{
    /// <summary>
    /// Computes what a reprice would do — no writes. <paramref name="eurCost"/> null
    /// falls back to the item's stored LastEurCost; <paramref name="rateOverride"/>
    /// null uses the effective (UI-set or config) EUR→TZS rate.
    /// </summary>
    Task<RepricePreviewLine> PreviewAsync(
        string itemCode, decimal? eurCost, decimal? rateOverride, bool includeTrace, CancellationToken ct);

    /// <summary>
    /// TARGET-PRICE mode: given a desired INCL-VAT selling price on ONE price list
    /// (1=Retail, 2=Dealer, 3=SuperDealer, 4=Maasai), back-solves the implied EUR
    /// invoice cost (binary search over the monotonic banded calculator) and returns
    /// the full forward preview at that cost — the remaining tiers recompute through
    /// the standard workflow. When the exact target is unreachable on the rounding
    /// grid, the nearest achievable price is returned with a warning.
    /// </summary>
    Task<(decimal? ImpliedEur, RepricePreviewLine Preview)> PreviewFromTargetAsync(
        string itemCode, int targetPl, decimal targetInclVat, decimal? rateOverride, CancellationToken ct);

    /// <summary>
    /// Applies a reprice: SAP price lists 1-4 (one Items.Update), Neon price rows,
    /// input stamps, audit log, and best-effort Odoo push. Follows the existing
    /// pricing workflow exactly — the only caller-supplied change is the EUR cost.
    /// </summary>
    Task<RepriceApplyResult> ApplyAsync(
        string itemCode, decimal? eurCost, decimal? rateOverride,
        string mode, string? note, CancellationToken ct);
}

public class LubesRepriceService : ILubesRepriceService
{
    /// <summary>Warn (never block) when a tier moves more than this percentage.</summary>
    private const decimal WarnThresholdPct = 30m;

    private readonly INeonProductRepository _neon;
    private readonly ILubesPricingRepository _pricingRepo;
    private readonly IPricingCalculator _calc;
    private readonly ISapB1Service _sap;
    private readonly IOdooService _odoo;
    private readonly ILogger<LubesRepriceService> _logger;

    public LubesRepriceService(
        INeonProductRepository neon,
        ILubesPricingRepository pricingRepo,
        IPricingCalculator calc,
        ISapB1Service sap,
        IOdooService odoo,
        ILogger<LubesRepriceService> logger)
    {
        _neon = neon;
        _pricingRepo = pricingRepo;
        _calc = calc;
        _sap = sap;
        _odoo = odoo;
        _logger = logger;
    }

    public async Task<RepricePreviewLine> PreviewAsync(
        string itemCode, decimal? eurCost, decimal? rateOverride, bool includeTrace, CancellationToken ct)
    {
        var warnings = new List<string>();
        var snapshot = await _neon.GetPricingSnapshotAsync(itemCode, ct);
        if (snapshot is null)
        {
            return new RepricePreviewLine(itemCode, false, null,
                "Item not found in NeonProducts — was it provisioned through the pipeline?",
                null, null, 0m, null, null, null, new(), null, null, null, warnings, null);
        }

        var rate = rateOverride ?? (await _pricingRepo.GetEffectiveRateAsync(ct)).Rate;

        var (eur, eurSource) = eurCost is > 0m
            ? (eurCost, "request")
            : snapshot.LastEurCost is > 0m
                ? (snapshot.LastEurCost, $"stored (last priced {snapshot.LastPricedAt:yyyy-MM-dd})")
                : ((decimal?)null, null);

        // Category — same resolution as provisioning: SAP group first, Odoo category fallback.
        string? category = null, categoryPath;
        if (snapshot.ItemGroupCode is int grp
            && _calc.TryPricingBandForSapGroup(grp) is string band)
        {
            category = band;
            categoryPath = $"SAP group {grp} ({snapshot.ItemGroupName ?? "?"}) → band '{band}' (authoritative)";
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.OdooCategoryName))
        {
            try
            {
                category = _calc.ResolvePricingCategory(snapshot.OdooCategoryName);
                categoryPath = $"Odoo category '{snapshot.OdooCategoryName}' → band '{category}' (fallback)";
            }
            catch (InvalidOperationException ex)
            {
                categoryPath = ex.Message;
            }
        }
        else
        {
            categoryPath = "No SAP group band and no Odoo category — cannot resolve a pricing band.";
        }

        string? error = null;
        if (eur is null)
            error = "No EUR cost: none supplied and no stored cost on this item — enter the EUR purchase price.";
        else if (category is null)
            error = $"Pricing category unresolvable: {categoryPath}";

        Dictionary<int, decimal>? newNet = null;
        Dictionary<int, decimal>? newInclVat = null;
        Dictionary<int, decimal?>? deltas = null;
        PricingTrace? trace = null;

        if (error is null)
        {
            trace = _calc.ComputeNetPricesWithTrace(eur!.Value * rate, category!);
            newNet = new Dictionary<int, decimal>
            {
                [1] = Math.Round(trace.Net.Retail, 2),
                [2] = Math.Round(trace.Net.Dealer, 2),
                [3] = Math.Round(trace.Net.SuperDealer, 2),
                [4] = Math.Round(trace.Net.Maasai, 2),
            };
            newInclVat = new Dictionary<int, decimal>
            {
                [1] = trace.RetailInclVat,
                [2] = trace.DealerInclVat,
                [3] = trace.SuperDealerInclVat,
                [4] = trace.MaasaiInclVat,
            };
            deltas = new Dictionary<int, decimal?>();
            foreach (var (pl, np) in newNet)
            {
                if (snapshot.StoredNetPrices.TryGetValue(pl, out var old) && old > 0m)
                {
                    var pct = Math.Round((np - old) / old * 100m, 1);
                    deltas[pl] = pct;
                    if (Math.Abs(pct) > WarnThresholdPct)
                        warnings.Add($"PL{pl} moves {pct:+0.#;-0.#}% ({old:N0} → {np:N0}) — beyond ±{WarnThresholdPct}%, double-check the EUR cost.");
                }
                else
                {
                    deltas[pl] = null;   // no stored price to compare against
                }
            }
        }

        return new RepricePreviewLine(
            itemCode, true, snapshot.ItemName, error,
            eur, eurSource, rate, eur is null ? null : eur.Value * rate,
            category, categoryPath,
            snapshot.StoredNetPrices, newNet, newInclVat, deltas, warnings,
            includeTrace ? trace : null);
    }

    public async Task<(decimal? ImpliedEur, RepricePreviewLine Preview)> PreviewFromTargetAsync(
        string itemCode, int targetPl, decimal targetInclVat, decimal? rateOverride, CancellationToken ct)
    {
        // Base preview resolves item existence, category, and the effective rate;
        // it may carry a "no EUR cost" error, which is irrelevant here.
        var basePrev = await PreviewAsync(itemCode, null, rateOverride, includeTrace: false, ct);
        if (!basePrev.Found)
            return (null, basePrev);
        if (basePrev.PricingCategory is null)
            return (null, basePrev with
            {
                Error = $"Cannot solve a target price: {basePrev.CategoryPath}"
            });

        var rate = basePrev.Rate;
        var category = basePrev.PricingCategory;

        decimal TierOf(decimal cif)
        {
            var t = _calc.ComputeNetPricesWithTrace(cif, category);
            return targetPl switch
            {
                1 => t.RetailInclVat,
                2 => t.DealerInclVat,
                3 => t.SuperDealerInclVat,
                4 => t.MaasaiInclVat,
                _ => t.RetailInclVat,
            };
        }

        // Every tier is a monotonic non-decreasing step function of CIF (ratios < 1
        // ⇒ price > cif, so the target itself bounds the search from above; widen
        // defensively in case of exotic ratio overrides).
        decimal lo = 0.01m, hi = targetInclVat;
        for (int i = 0; i < 6 && TierOf(hi) < targetInclVat; i++) hi *= 2m;
        if (TierOf(hi) < targetInclVat)
            return (null, basePrev with { Error = $"Target PL{targetPl} {targetInclVat:N0} is unreachable with the current ratios." });

        for (int i = 0; i < 64 && hi - lo > 0.005m; i++)
        {
            var mid = (lo + hi) / 2m;
            if (TierOf(mid) >= targetInclVat) hi = mid; else lo = mid;
        }
        var cif = hi;   // minimal CIF whose tier output reaches the target

        var impliedEur = Math.Round(cif / rate, 2, MidpointRounding.AwayFromZero);
        // 2-dp rounding of the EUR can nudge CIF below a rounding boundary — bump a cent.
        if (impliedEur <= 0m) impliedEur = 0.01m;
        if (TierOf(impliedEur * rate) < targetInclVat) impliedEur += 0.01m;

        var preview = await PreviewAsync(itemCode, impliedEur, rateOverride, includeTrace: false, ct);
        var achieved = preview.NewInclVat is not null && preview.NewInclVat.TryGetValue(targetPl, out var a)
            ? a : (decimal?)null;
        if (achieved is { } got && got != targetInclVat)
            preview.Warnings.Add(
                $"Target PL{targetPl} of {targetInclVat:N0} is not exactly reachable on the banded rounding grid — " +
                $"nearest achievable is {got:N0}.");

        _logger.LogInformation(
            "Target-price solve for {ItemCode}: PL{Pl} target {Target} → implied EUR {Eur} (CIF {Cif}), achieved {Achieved}",
            itemCode, targetPl, targetInclVat, impliedEur, impliedEur * rate, achieved);

        return (impliedEur, preview);
    }

    public async Task<RepriceApplyResult> ApplyAsync(
        string itemCode, decimal? eurCost, decimal? rateOverride,
        string mode, string? note, CancellationToken ct)
    {
        var preview = await PreviewAsync(itemCode, eurCost, rateOverride, includeTrace: false, ct);
        if (!preview.CanApply)
            return new RepriceApplyResult(preview, false, new(),
                preview.Error ?? "Preview did not produce prices to apply.");

        var n = preview.NewNet!;

        // 1. SAP — all four lists in one Items.Update (0-based indexes).
        await _sap.SetPriceListPricesAsync(itemCode, new Dictionary<int, decimal>
        {
            [0] = n[1], [1] = n[2], [2] = n[3], [3] = n[4],
        }, ct);

        // 2. Neon price rows.
        await _neon.UpsertPricesAsync(itemCode, n[1], n[2], n[3], n[4], ct);

        // 3. Stamp the inputs so future repricing and drift checks have them.
        await _pricingRepo.StampPricingInputsAsync(itemCode, preview.EurCost!.Value, preview.Rate, ct);

        // 4. Audit log.
        var old = preview.OldNet;
        await _pricingRepo.LogChangeAsync(new PriceChangeEntry(
            itemCode,
            old.TryGetValue(1, out var o1) ? o1 : null,
            old.TryGetValue(2, out var o2) ? o2 : null,
            old.TryGetValue(3, out var o3) ? o3 : null,
            old.TryGetValue(4, out var o4) ? o4 : null,
            n[1], n[2], n[3], n[4],
            preview.EurCost, preview.Rate, preview.PricingCategory,
            mode, note, DateTime.UtcNow), ct);

        // 5. Odoo — best-effort; failures become notes, never roll back SAP/Neon.
        List<string> odooNotes;
        try
        {
            odooNotes = await _odoo.UpdateLubesPricesAsync(itemCode, n[1], n[2], n[3], n[4]);
        }
        catch (Exception ex)
        {
            odooNotes = new List<string> { $"Odoo update failed: {ex.Message}" };
            _logger.LogWarning(ex, "Odoo price push failed for {ItemCode} — SAP/Neon are updated.", itemCode);
        }

        _logger.LogInformation(
            "Repriced {ItemCode} (mode={Mode}): EUR {Eur} × {Rate} → PL1={P1} PL2={P2} PL3={P3} PL4={P4}",
            itemCode, mode, preview.EurCost, preview.Rate, n[1], n[2], n[3], n[4]);

        return new RepriceApplyResult(preview, true, odooNotes, null);
    }
}
