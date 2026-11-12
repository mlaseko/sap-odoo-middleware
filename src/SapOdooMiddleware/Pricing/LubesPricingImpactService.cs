using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Pricing;

/// <summary>One item in the formula impact report: stored prices against what the formula gives now.</summary>
public record PricingImpactRow(
    string ItemCode,
    string? ItemName,
    int? ItemGroupCode,
    string? OdooCategoryName,
    string? PricingBand,
    decimal? EurCost,
    decimal Rate,
    decimal? CifTzs,
    Dictionary<int, decimal> StoredNet,
    Dictionary<int, decimal>? NewNet,
    Dictionary<int, decimal>? NewInclVat,
    Dictionary<int, decimal?>? DeltaPct,
    List<string> Reasons,
    string? Error,
    /// <summary>A grease still in the shared SAP group 107 (should move to 117).</summary>
    bool GreaseInGroup107)
{
    /// <summary>True when the formula gives a price different from the stored one on any price list.</summary>
    public bool PricesChange => DeltaPct is not null && DeltaPct.Values.Any(d => d is null || d != 0m);
}

/// <summary>Result of one impact-report run.</summary>
public record PricingImpactReport(
    DateTime GeneratedAt,
    string RateMode,
    decimal CurrentRate,
    int Scanned,
    int Changed,
    int Oscillating,
    int Floored,
    int GreasesInGroup107,
    int Errors,
    List<PricingImpactRow> Rows);

public interface ILubesPricingImpactService
{
    /// <summary>
    /// READ ONLY. Recomputes every Lubes item that has a stored EUR cost (and every item in SAP group
    /// 107) with the current calculator and reports the ones whose prices would change, and why.
    /// <paramref name="useStoredRate"/> = true prices each item at the EUR→TZS rate it was last priced
    /// with, so only the formula changes show; false uses today's rate (what a reprice would post).
    /// </summary>
    Task<PricingImpactReport> BuildAsync(bool useStoredRate, CancellationToken ct);
}

public sealed class LubesPricingImpactService : ILubesPricingImpactService
{
    private const int SharedGearOilsGreasesGroup = 107;

    private readonly ILubesPricingRepository _repo;
    private readonly IPricingCalculator _calc;

    public LubesPricingImpactService(ILubesPricingRepository repo, IPricingCalculator calc)
    {
        _repo = repo;
        _calc = calc;
    }

    public async Task<PricingImpactReport> BuildAsync(bool useStoredRate, CancellationToken ct)
    {
        var current = (await _repo.GetEffectiveRateAsync(ct)).Rate;
        var sources = await _repo.GetImpactSourcesAsync(new[] { SharedGearOilsGreasesGroup }, ct);

        var all = sources.Select(s => Analyze(_calc, s, useStoredRate, current)).ToList();
        var shown = all
            .Where(r => r.PricesChange || r.GreaseInGroup107 || r.Error is not null || r.Reasons.Count > 0)
            .ToList();

        return new PricingImpactReport(
            DateTime.UtcNow,
            useStoredRate ? "stored rate per item (formula effect only)" : $"current rate {current:N0}",
            current,
            Scanned: all.Count,
            Changed: all.Count(r => r.PricesChange),
            Oscillating: all.Count(r => r.Reasons.Any(x => x.StartsWith("Band", StringComparison.Ordinal))),
            Floored: all.Count(r => r.Reasons.Any(x => x.StartsWith("No-drop", StringComparison.Ordinal))),
            GreasesInGroup107: all.Count(r => r.GreaseInGroup107),
            Errors: all.Count(r => r.Error is not null),
            Rows: shown);
    }

    /// <summary>Pure computation for one item (unit-testable).</summary>
    internal static PricingImpactRow Analyze(
        IPricingCalculator calc, PricingImpactSource s, bool useStoredRate, decimal currentRate)
    {
        var reasons = new List<string>();
        var resolution = calc.ResolveBandForItem(
            s.ItemGroupCode, s.ItemGroupName, s.OdooCategoryName, s.OdooCategoryExternalId);

        var greaseIn107 = s.ItemGroupCode == SharedGearOilsGreasesGroup && resolution.Band == "Greases";
        if (greaseIn107)
            reasons.Add("Grease in SAP group 107: now priced as Greases (was Gear Oils). Move it to group 117.");

        var rate = useStoredRate && s.LastEurTzsRate is > 0m ? s.LastEurTzsRate.Value : currentRate;

        string? error = null;
        if (resolution.Band is null)
            error = $"No pricing band: {resolution.Path}";
        else if (s.LastEurCost is not > 0m)
            error = "No stored EUR cost — reprice it from the item lookup with its EUR invoice price.";

        if (error is not null)
            return new PricingImpactRow(s.ItemCode, s.ItemName, s.ItemGroupCode, s.OdooCategoryName,
                resolution.Band, s.LastEurCost, rate, null, s.StoredNetPrices, null, null, null,
                reasons, error, greaseIn107);

        var cif = s.LastEurCost!.Value * rate;
        var trace = calc.ComputeNetPricesWithTrace(cif, resolution.Band!);
        if (trace.BandResolution != "converged")
            reasons.Add($"Band choice: {trace.BandResolution}.");
        if (trace.MonotonicFloorApplied)
            reasons.Add("No-drop floor: " + trace.FloorNote);

        var newNet = new Dictionary<int, decimal>
        {
            [1] = Math.Round(trace.Net.Retail, 2),
            [2] = Math.Round(trace.Net.Dealer, 2),
            [3] = Math.Round(trace.Net.SuperDealer, 2),
            [4] = Math.Round(trace.Net.Maasai, 2),
        };
        var newIncl = new Dictionary<int, decimal>
        {
            [1] = trace.RetailInclVat, [2] = trace.DealerInclVat,
            [3] = trace.SuperDealerInclVat, [4] = trace.MaasaiInclVat,
        };

        // Stored prices are 2-dp nets of whole-shilling shelf prices: under 1 TZS is rounding, not a change.
        var deltas = new Dictionary<int, decimal?>();
        foreach (var (pl, np) in newNet)
        {
            if (s.StoredNetPrices.TryGetValue(pl, out var old) && old > 0m)
                deltas[pl] = Math.Abs(np - old) < 1m ? 0m : Math.Round((np - old) / old * 100m, 1);
            else
                deltas[pl] = null;
        }

        var changed = deltas.Values.Any(d => d is null || d != 0m);
        if (changed && reasons.Count == 0)
            reasons.Add(useStoredRate
                ? "Stored prices differ from the formula at the stored cost and rate (a manual edit, a ratio change, or priced before the rate was stamped)."
                : "Rate and/or formula: today's rate differs from the one the item was priced at.");

        return new PricingImpactRow(s.ItemCode, s.ItemName, s.ItemGroupCode, s.OdooCategoryName,
            resolution.Band, s.LastEurCost, rate, cif, s.StoredNetPrices, newNet, newIncl, deltas,
            reasons, null, greaseIn107);
    }
}
