using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Pricing;

/// <summary>
/// One item to price that need not exist in SAP, Neon or Odoo. The pricing band comes from
/// <see cref="Band"/> (a band name or alias), else <see cref="SapGroup"/> (+ <see cref="OdooCategory"/>
/// for the shared group 107), else <see cref="OdooCategory"/> alone.
/// </summary>
public record SimulationInput(
    string? Reference,
    string? Description,
    decimal Cost,
    string Currency = "EUR",
    string? Band = null,
    int? SapGroup = null,
    string? OdooCategory = null,
    string? Benchmark = null);

/// <summary>Simulated prices for one item. Nothing is written anywhere.</summary>
public record SimulationResult(
    SimulationInput Input,
    decimal Rate,
    decimal? CostTzs,
    string? Band,
    string BandPath,
    /// <summary>Price list 1-4 → incl-VAT shelf price.</summary>
    Dictionary<int, decimal>? InclVat,
    /// <summary>Price list 1-4 → net (excl-VAT) price, as it would be stored.</summary>
    Dictionary<int, decimal>? Net,
    /// <summary>Price list 1-4 → net margin over the TZS cost, in percent.</summary>
    Dictionary<int, decimal>? MarginPct,
    List<string> Notes,
    List<string> Warnings,
    string? Error,
    RepricePreviewLine? Benchmark,
    PricingTrace? Trace)
{
    public bool Ok => Error is null && InclVat is not null;
}

public interface ILubesPriceSimulator
{
    /// <summary>
    /// Prices each input with the live calculator (current ratios, top-band markups, high-cost cap,
    /// Maasai floor). EUR costs convert at <paramref name="rateOverride"/> or the effective rate.
    /// Read only: nothing is posted to SAP, Neon or Odoo, and nothing is saved.
    /// </summary>
    Task<List<SimulationResult>> SimulateAsync(
        IReadOnlyList<SimulationInput> inputs, decimal? rateOverride, CancellationToken ct);
}

public sealed class LubesPriceSimulator : ILubesPriceSimulator
{
    public const int MaxItems = 500;
    private static readonly string[] Currencies = { "EUR", "TZS" };

    private readonly IPricingCalculator _calc;
    private readonly ILubesPricingRepository _pricingRepo;
    private readonly ILubesRepriceService _reprice;
    private readonly decimal _marginWarnPct;

    public LubesPriceSimulator(
        IPricingCalculator calc, ILubesPricingRepository pricingRepo, ILubesRepriceService reprice,
        Microsoft.Extensions.Options.IOptions<Configuration.PricingSettings> settings)
    {
        _calc = calc;
        _pricingRepo = pricingRepo;
        _reprice = reprice;
        _marginWarnPct = settings.Value.MarginWarnPct;
    }

    public async Task<List<SimulationResult>> SimulateAsync(
        IReadOnlyList<SimulationInput> inputs, decimal? rateOverride, CancellationToken ct)
    {
        if (inputs.Count > MaxItems)
            throw new InvalidOperationException($"{inputs.Count} items exceed the {MaxItems}-item simulation cap.");

        var rate = rateOverride is > 0m ? rateOverride.Value : (await _pricingRepo.GetEffectiveRateAsync(ct)).Rate;
        var benchCache = new Dictionary<string, RepricePreviewLine>(StringComparer.OrdinalIgnoreCase);
        var results = new List<SimulationResult>();

        foreach (var input in inputs)
        {
            var r = Simulate(_calc, input, rate, _marginWarnPct);
            if (!string.IsNullOrWhiteSpace(input.Benchmark))
            {
                var key = input.Benchmark.Trim();
                if (!benchCache.TryGetValue(key, out var bench))
                {
                    bench = await _reprice.PreviewAsync(key, null, null, includeTrace: false, ct);
                    benchCache[key] = bench;
                }
                r = r with { Benchmark = bench };
                if (!bench.Found)
                    r.Warnings.Add($"Benchmark '{key}' not found — comparison skipped.");
            }
            results.Add(r);
        }
        return results;
    }

    /// <summary>Pure pricing of one input (unit-testable; no I/O).</summary>
    internal static SimulationResult Simulate(
        IPricingCalculator calc, SimulationInput input, decimal rate, decimal marginWarnPct)
    {
        var notes = new List<string>();
        var warnings = new List<string>();
        var currency = (input.Currency ?? "EUR").Trim().ToUpperInvariant();

        SimulationResult Fail(string error, string? band = null, string path = "") =>
            new(input, rate, null, band, path, null, null, null, notes, warnings, error, null, null);

        if (Array.IndexOf(Currencies, currency) < 0)
            return Fail($"Currency '{input.Currency}' is not supported — use EUR or TZS.");
        if (input.Cost <= 0m)
            return Fail("Enter a cost greater than zero.");
        if (currency == "EUR" && rate <= 0m)
            return Fail("No EUR→TZS rate is set.");

        // Band: explicit band → SAP group (+ Odoo category) → Odoo category.
        string? band = null;
        string path;
        if (!string.IsNullOrWhiteSpace(input.Band))
        {
            try
            {
                band = calc.ResolvePricingCategory(input.Band);
                path = $"Band '{band}' (chosen)";
            }
            catch (InvalidOperationException ex)
            {
                return Fail(ex.Message, null, $"Band '{input.Band}' is not a pricing band.");
            }
        }
        else
        {
            var res = calc.ResolveBandForItem(input.SapGroup, null, input.OdooCategory, null);
            band = res.Band;
            path = res.Path;
            if (band is null)
                return Fail($"No pricing band: {res.Path} Pick a band, a SAP group or an Odoo category.", null, res.Path);
        }

        var costTzs = currency == "TZS" ? input.Cost : input.Cost * rate;
        var t = calc.ComputeNetPricesWithTrace(costTzs, band);
        if (t.HasNonPositivePrice)
            return Fail($"The cost {costTzs:N0} TZS is too small for the pricing formula: it gives a zero or negative price.",
                band, path);

        var incl = new Dictionary<int, decimal>
        {
            [1] = t.RetailInclVat, [2] = t.DealerInclVat, [3] = t.SuperDealerInclVat, [4] = t.MaasaiInclVat,
        };
        var net = incl.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value / 1.18m, 2));
        var margin = net.ToDictionary(kv => kv.Key, kv => Math.Round((kv.Value - costTzs) / costTzs * 100m, 1));

        if (currency == "EUR") notes.Add($"€{input.Cost:N2} × {rate:N0} = {costTzs:N0} TZS (supplier cost only).");
        if (t.BandResolution != "converged") notes.Add($"Band choice: {t.BandResolution}.");
        if (t.MonotonicFloorApplied && t.FloorNote is not null) notes.Add(t.FloorNote);
        if (t.CapNote is not null) notes.Add(t.CapNote);
        if (t.MaasaiFloorNote is not null) notes.Add(t.MaasaiFloorNote);
        warnings.AddRange(LubesRepriceService.MarginWarnings(net, costTzs, marginWarnPct));

        return new SimulationResult(input, rate, costTzs, band, path, incl, net, margin, notes, warnings, null, null, t);
    }
}
