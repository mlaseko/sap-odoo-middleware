namespace SapOdooMiddleware.Pricing;

/// <summary>Four net (excl-VAT) prices in TZS.</summary>
public record PriceTiers(decimal Retail, decimal Dealer, decimal SuperDealer, decimal Maasai);

/// <summary>One band-convergence pass of the pricing algorithm (diagnostics).</summary>
public record PricingTraceStep(
    int Iteration, string Band,
    decimal SpRatio, decimal DealerRatio, decimal RetailRatio,
    decimal SpInclVat, decimal DealerInclVat, decimal RetailInclVat);

/// <summary>Full derivation of one pricing computation (diagnostics / trace endpoint).</summary>
public record PricingTrace(
    decimal CifCostTzs,
    string PricingCategory,
    List<PricingTraceStep> Iterations,
    string FinalBand,
    bool OrderingAdjusted,
    decimal RetailInclVat,
    decimal DealerInclVat,
    decimal SuperDealerInclVat,
    string MaasaiCategory,
    int MaasaiBandIndex,
    decimal MaasaiRatio,
    decimal MaasaiInclVat,
    PriceTiers Net,
    /// <summary>How the band was chosen: "converged", or the oscillation and which band was used.</summary>
    string BandResolution = "converged",
    /// <summary>True when a tier was lifted so a higher cost never gives a lower price.</summary>
    bool MonotonicFloorApplied = false,
    /// <summary>The prices before the no-drop floor was applied (incl-VAT; null when no floor applied).</summary>
    string? FloorNote = null,
    /// <summary>True when any tier came out zero or negative (cost too small for the formula).</summary>
    bool HasNonPositivePrice = false,
    /// <summary>How the high-cost cap was applied, or null when the cost is below the cap start.</summary>
    string? CapNote = null,
    /// <summary>Set when Maasai was raised to the cost floor (cost + MaasaiMinMarginPct, excl. VAT).</summary>
    string? MaasaiFloorNote = null);

/// <summary>
/// High-cost cap ("option 5b", Nov 2026). Costs up to <see cref="StartCostTzs"/> price through the
/// bands as usual. Above it, the price at the cap start is kept and only the EXTRA cost is marked up,
/// at these smaller multiples (VAT incl.). Prices and profit keep rising with cost while the overall
/// markup tapers (e.g. 2.10x at EUR 400 → ~1.68x at EUR 1,000 → ~1.59x at EUR 1,500).
/// </summary>
public sealed record PricingCap(
    decimal StartCostTzs = 1_260_000m,
    decimal Retail = 1.40m,
    decimal Dealer = 1.35m,
    decimal SuperDealer = 1.28m)
{
    public bool Enabled => StartCostTzs > 0m;
}

/// <summary>Which pricing band an item uses and why (for display).</summary>
public record PricingBandResolution(string? Band, string Path);

/// <summary>A runtime override of one category+band's three tier ratios.</summary>
public record BandRatioOverride(string Category, string Band, decimal Sp, decimal Dealer, decimal Retail);

/// <summary>A runtime override of one category+band's Maasai (PL04÷PL03) ratio. BandIndex 0-3 = A-D.</summary>
public record MaasaiRatioOverride(string Category, int BandIndex, decimal Ratio);

/// <summary>One category+band row in the ratio snapshot (with whether it deviates from the default).</summary>
public record BandRatioRow(
    string Category, string Band,
    decimal Sp, decimal Dealer, decimal Retail,
    bool IsOverridden);

/// <summary>One category+Maasai-band row in the ratio snapshot.</summary>
public record MaasaiRatioRow(string Category, int BandIndex, string BandLabel, decimal Ratio, bool IsOverridden);

/// <summary>The calculator's full effective ratio state (for the editor UI).</summary>
public record PricingRatioSnapshot(
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Bands,
    IReadOnlyList<string> MaasaiBandLabels,
    IReadOnlyList<BandRatioRow> BandRatios,
    IReadOnlyList<MaasaiRatioRow> MaasaiRatios);

public interface IPricingCalculator
{
    /// <summary>Compute Retail/Dealer/Super-Dealer NET (excl-VAT) prices from CIF cost in TZS.</summary>
    PriceTiers ComputeNetPrices(decimal cifCostTzs, string pricingCategory);

    /// <summary>Same computation as <see cref="ComputeNetPrices"/>, returning the full derivation.</summary>
    PricingTrace ComputeNetPricesWithTrace(decimal cifCostTzs, string pricingCategory);

    /// <summary>The calculator's current effective ratio tables (defaults + applied overrides).</summary>
    PricingRatioSnapshot GetRatioSnapshot();

    /// <summary>
    /// Replaces the active override set: effective tables become the hard-coded
    /// defaults with these overrides applied on top (atomically, thread-safe).
    /// </summary>
    void ApplyRatioOverrides(
        IReadOnlyList<BandRatioOverride> bandOverrides,
        IReadOnlyList<MaasaiRatioOverride> maasaiOverrides);

    /// <summary>
    /// What-if computation: runs the full trace with an EPHEMERAL ratio override on
    /// top of the current effective tables — global state is not touched. Used by the
    /// ratio editor's Simulate before an override is approved and saved.
    /// </summary>
    PricingTrace Simulate(
        decimal cifCostTzs, string pricingCategory,
        BandRatioOverride? tempBand, MaasaiRatioOverride? tempMaasai);

    /// <summary>Resolve a scraped/LM category string to a canonical pricing-category key.</summary>
    string ResolvePricingCategory(string? scrapedCategory);

    /// <summary>
    /// Pricing band for a SAP/OITB item-group code, or null if that group has no dedicated band
    /// (caller should then fall back to <see cref="ResolvePricingCategory"/>). Pricing is keyed off the
    /// authoritative SAP group rather than the noisy Odoo category so siblings price identically.
    /// </summary>
    string? TryPricingBandForSapGroup(int sapGroupCode);

    /// <summary>
    /// Like <see cref="TryPricingBandForSapGroup(int)"/>, but aware of the Odoo category for SAP groups
    /// that are shared between pricing bands: a grease still sitting in 107 ("Gear Oils/ATF/Greases")
    /// prices as Greases, not Gear Oils. <paramref name="odooCategory"/> may be a full path, a leaf
    /// name or a LIQUI MOLY external id.
    /// </summary>
    string? TryPricingBandForSapGroup(int sapGroupCode, string? odooCategory);

    /// <summary>
    /// The single band-resolution rule used by reprice, trace and the impact report: SAP group first
    /// (Odoo-category-aware for shared groups), else the Odoo category. Band is null when unresolvable.
    /// </summary>
    PricingBandResolution ResolveBandForItem(
        int? sapGroupCode, string? sapGroupName, string? odooCategoryName, string? odooCategoryExternalId);

    /// <summary>
    /// Compute the Maasai (PL04) NET price from an existing Super-Dealer (PL03) NET price.
    /// Used by the backfill endpoint to derive PL04 for items that already have PL03.
    /// </summary>
    decimal ComputeMaasaiNetFromPl03Net(decimal pl03Net, string pricingCategory);
}

/// <summary>
/// Faithful port of the Molas pricing tool's calcFromCost(cost, category) JS function.
/// Algorithm: iterate band selection (max 5 passes); per band r:
///   sp     = ceil( (cost/r.sp) / 1000 ) * 1000
///   dealer = round((cost/r.d ) / 5000) * 5000     (round = half-away-from-zero, matching JS)
///   retail = floor((cost/r.r ) / 5000) * 5000
///   recompute band from sp; converge.
/// Then enforce sp &lt; dealer &lt; retail. Returned values are then divided by 1.18 to
/// store NET (excl-VAT) prices in SAP/Odoo price lists.
///
/// Two deliberate departures from the HTML tool (Nov 2026):
///   1. When the band walk never settles (two bands point at each other), the HTML tool kept
///      whichever band pass 5 landed on. Here the walk stops at the first repeated band and the
///      loop band with the HIGHER price is used, so a cost always gives the same price.
///   2. No-drop floor: at some band edges the next band's ratios gave a LOWER price for a higher
///      cost. Each tier (and Maasai) is lifted to the highest price any lower cost produces.
///   3. Top bands and high-cost cap ("option 5b"): 600k-900k and 1M+ use Retail 2.10x / Dealer 2.00x
///      / SD 1.85x cost for every category, and above <see cref="PricingCap.StartCostTzs"/> only the
///      extra cost is marked up, at the smaller cap multiples (see <see cref="PricingCap"/>).
/// Below the 600k-900k band, where the walk settles and no drop exists, prices are identical to
/// the HTML tool.
/// </summary>
public class PricingCalculator : IPricingCalculator
{
    private const decimal VAT = 1.18m;

    private readonly PricingCap _cap;
    private readonly decimal _maasaiMinMarginPct;

    /// <summary>Defaults (cap from 1,260,000 TZS ≈ EUR 400 at 3,150; Maasai ≥ cost + 5%).</summary>
    public PricingCalculator() : this(new PricingCap()) { }

    /// <param name="cap">High-cost cap.</param>
    /// <param name="maasaiMinMarginPct">
    /// Maasai (PL04) floor: its net (excl-VAT) price is never below cost × (1 + this %). Maasai follows
    /// Super Dealer × a ratio, so on capped (expensive) items it could otherwise fall to or below cost.
    /// Negative disables the floor.
    /// </param>
    public PricingCalculator(PricingCap cap, decimal maasaiMinMarginPct = 5m)
    {
        _maasaiMinMarginPct = maasaiMinMarginPct;
        if (cap.Enabled && !(cap.SuperDealer < cap.Dealer && cap.Dealer < cap.Retail && cap.SuperDealer >= VAT))
            throw new ArgumentException(
                "Pricing cap multiples must satisfy VAT (1.18) <= SuperDealer < Dealer < Retail, "
                + "so tiers stay ordered and the extra cost is never sold below cost.", nameof(cap));
        _cap = cap;
    }

    /// <summary>The active high-cost cap (for display).</summary>
    public PricingCap Cap => _cap;

    /// <summary>The Maasai cost floor in percent over cost (negative = off).</summary>
    public decimal MaasaiMinMarginPct => _maasaiMinMarginPct;

    /// <summary>
    /// Raises Maasai to cost × (1 + floor%) incl. VAT, rounded up to 1 000, but always at least 1 000
    /// below Super Dealer. Both inputs rise with cost, so the no-drop guarantee still holds.
    /// </summary>
    private PricingTrace ApplyMaasaiFloor(PricingTrace t)
    {
        if (_maasaiMinMarginPct < 0m || t.SuperDealerInclVat <= 0m) return t;
        var floorIncl = Math.Ceiling(t.CifCostTzs * (1m + _maasaiMinMarginPct / 100m) * VAT / 1000m) * 1000m;
        var target = Math.Min(floorIncl, t.SuperDealerInclVat - 1000m);
        if (target <= t.MaasaiInclVat) return t;
        return t with
        {
            MaasaiInclVat = target,
            Net = t.Net with { Maasai = target / VAT },
            MaasaiFloorNote = $"Maasai raised from {t.MaasaiInclVat:N0} to {target:N0} (incl VAT) so it stays at least "
                              + $"{_maasaiMinMarginPct:0.#}% over the cost"
                              + (target < floorIncl ? " — capped just below Super Dealer" : "") + ".",
        };
    }

    // Lower bound inclusive, upper bound exclusive.
    private static readonly (decimal Lo, decimal Hi, string Label)[] BandDefs =
    {
        (0m,       25_000m,      "up to 25,000"),
        (25_000m,  50_000m,      "25k to 50k"),
        (50_000m,  75_000m,      "50k to 75k"),
        (75_000m,  100_000m,     "75k to 100k"),
        (100_000m, 200_000m,     "100k to 200k"),
        (200_000m, 350_000m,     "200k to 350k"),
        (350_000m, 600_000m,     "350k to 600k"),
        (600_000m, 900_000m,     "600k to 900k"),
        (900_000m, decimal.MaxValue, "1M and above"),
    };

    private static string GetBand(decimal sp)
    {
        foreach (var (lo, hi, label) in BandDefs)
            if (sp >= lo && sp < hi) return label;
        return "1M and above";
    }

    // (sp, d, r) DEFAULT ratios (the ported HTML tool). Cost = price × ratio, so
    // NOTE (Nov 2026, "option 5b"): the 600k-900k and 1M+ rows are the same for every category:
    // Retail 2.10x / Dealer 2.00x / Super Dealer 1.85x cost (TopBandRatios). The original rows spiked
    // to ~3.8x in 600k-900k and fell to 1.57x in 1M+, so a dearer item could sell for less. Above the
    // cap start (Pricing:CapStartCostTzs) only the part of the cost above it gets the smaller cap
    // markups — see ApplyCap. The old values are kept in the comments.
    //
    // price = cost / ratio. Runtime overrides are applied on top via
    // ApplyRatioOverrides; the effective tables live in _bandRatios/_maasaiRatios.
    /// <summary>600k-900k and 1M+ rows for every category: SD 1.85x, Dealer 2.00x, Retail 2.10x cost.</summary>
    private static readonly (decimal sp, decimal d, decimal r) TopBandRatios =
        (0.540540540540m, 0.5m, 0.476190476190m);

    private static readonly IReadOnlyDictionary<string,
        IReadOnlyDictionary<string, (decimal sp, decimal d, decimal r)>> DefaultBandRatios
        = new Dictionary<string, IReadOnlyDictionary<string, (decimal, decimal, decimal)>>
    {
        ["Additives"] = new Dictionary<string, (decimal, decimal, decimal)>
        {
            ["up to 25,000"] = (0.437914m, 0.27066m,  0.235839m),
            ["25k to 50k"]   = (0.410656m, 0.284844m, 0.243509m),
            ["50k to 75k"]   = (0.462499m, 0.346729m, 0.30887m),
            ["75k to 100k"]  = (0.572189m, 0.432457m, 0.418569m),
            ["100k to 200k"] = (0.574126m, 0.453004m, 0.425525m),
            ["200k to 350k"] = (0.655739m, 0.534233m, 0.523511m),
            ["350k to 600k"] = (0.690027m, 0.578948m, 0.562213m),
            ["600k to 900k"] = TopBandRatios, // was (0.442655m, 0.30166m, 0.265692m)
            ["1M and above"] = TopBandRatios, // was (0.708817m, 0.663222m, 0.636483m)
        },
        ["Engine Oils"] = new Dictionary<string, (decimal, decimal, decimal)>
        {
            ["up to 25,000"] = (0.480885m, 0.29823m,  0.27119m),
            ["25k to 50k"]   = (0.540667m, 0.395182m, 0.345771m),
            ["50k to 75k"]   = (0.49142m,  0.36409m,  0.335015m),
            ["75k to 100k"]  = (0.572189m, 0.432457m, 0.418569m),
            ["100k to 200k"] = (0.592899m, 0.474121m, 0.443912m),
            ["200k to 350k"] = (0.655739m, 0.534233m, 0.523511m),
            ["350k to 600k"] = (0.638425m, 0.546693m, 0.536605m),
            ["600k to 900k"] = TopBandRatios, // was (0.5862m, 0.464878m, 0.428126m)
            ["1M and above"] = TopBandRatios, // was (0.708817m, 0.663222m, 0.636483m)
        },
        ["Gear Oils & Transmission Fluids"] = new Dictionary<string, (decimal, decimal, decimal)>
        {
            ["up to 25,000"] = (0.480885m, 0.29823m,  0.27119m),
            ["25k to 50k"]   = (0.518117m, 0.357632m, 0.322287m),
            ["50k to 75k"]   = (0.534855m, 0.397481m, 0.367747m),
            ["75k to 100k"]  = (0.572189m, 0.432457m, 0.418569m),
            ["100k to 200k"] = (0.574126m, 0.453004m, 0.425525m),
            ["200k to 350k"] = (0.655739m, 0.534233m, 0.523511m),
            ["350k to 600k"] = (0.690027m, 0.578948m, 0.562213m),
            ["600k to 900k"] = TopBandRatios, // was (0.57413m, 0.426881m, 0.401149m)
            ["1M and above"] = TopBandRatios, // was (0.708817m, 0.663222m, 0.636483m)
        },
        ["Greases"] = new Dictionary<string, (decimal, decimal, decimal)>
        {
            ["up to 25,000"] = (0.480885m, 0.29823m,  0.27119m),
            ["25k to 50k"]   = (0.503494m, 0.355078m, 0.314078m),
            ["50k to 75k"]   = (0.49142m,  0.36409m,  0.335015m),
            ["75k to 100k"]  = (0.572189m, 0.432457m, 0.418569m),
            ["100k to 200k"] = (0.574126m, 0.453004m, 0.425525m),
            ["200k to 350k"] = (0.655739m, 0.534233m, 0.523511m),
            ["350k to 600k"] = (0.690027m, 0.578948m, 0.562213m),
            ["600k to 900k"] = TopBandRatios, // was (0.593563m, 0.390056m, 0.341299m)
            ["1M and above"] = TopBandRatios, // was (0.708817m, 0.663222m, 0.636483m)
        },
        ["Oils (Industrial/Other Fluids)"] = new Dictionary<string, (decimal, decimal, decimal)>
        {
            ["up to 25,000"] = (0.480885m, 0.29823m,  0.27119m),
            ["25k to 50k"]   = (0.503494m, 0.355078m, 0.314078m),
            ["50k to 75k"]   = (0.49142m,  0.36409m,  0.335015m),
            ["75k to 100k"]  = (0.572189m, 0.432457m, 0.418569m),
            ["100k to 200k"] = (0.574126m, 0.453004m, 0.425525m),
            ["200k to 350k"] = (0.655739m, 0.534233m, 0.523511m),
            ["350k to 600k"] = (0.690027m, 0.578948m, 0.562213m),
            ["600k to 900k"] = TopBandRatios, // was (0.655489m, 0.501236m, 0.478683m)
            ["1M and above"] = TopBandRatios, // was (0.708817m, 0.663222m, 0.636483m)
        },
        ["Service"] = new Dictionary<string, (decimal, decimal, decimal)>
        {
            ["up to 25,000"] = (0.377929m, 0.26819m,  0.226757m),
            ["25k to 50k"]   = (0.464615m, 0.306836m, 0.274957m),
            ["50k to 75k"]   = (0.49142m,  0.36409m,  0.335015m),
            ["75k to 100k"]  = (0.572189m, 0.432457m, 0.418569m),
            ["100k to 200k"] = (0.574126m, 0.453004m, 0.425525m),
            ["200k to 350k"] = (0.655739m, 0.534233m, 0.523511m),
            ["350k to 600k"] = (0.690027m, 0.578948m, 0.562213m),
            ["600k to 900k"] = TopBandRatios, // was (0.445547m, 0.304273m, 0.271382m)
            ["1M and above"] = TopBandRatios, // was (0.708817m, 0.663222m, 0.636483m)
        },
        ["Vehicle Care"] = new Dictionary<string, (decimal, decimal, decimal)>
        {
            ["up to 25,000"] = (0.551012m, 0.331218m, 0.31546m),
            ["25k to 50k"]   = (0.503494m, 0.355078m, 0.314078m),
            ["50k to 75k"]   = (0.49142m,  0.36409m,  0.335015m),
            ["75k to 100k"]  = (0.572189m, 0.432457m, 0.418569m),
            ["100k to 200k"] = (0.574126m, 0.453004m, 0.425525m),
            ["200k to 350k"] = (0.655739m, 0.534233m, 0.523511m),
            ["350k to 600k"] = (0.690027m, 0.578948m, 0.562213m),
            ["600k to 900k"] = TopBandRatios, // was (0.538114m, 0.332442m, 0.31608m)
            ["1M and above"] = TopBandRatios, // was (0.708817m, 0.663222m, 0.636483m)
        },
        ["Workshop Pro-Line"] = new Dictionary<string, (decimal, decimal, decimal)>
        {
            ["up to 25,000"] = (0.480885m, 0.29823m,  0.27119m),
            ["25k to 50k"]   = (0.436071m, 0.298225m, 0.270966m),
            ["50k to 75k"]   = (0.463275m, 0.335502m, 0.316378m),
            ["75k to 100k"]  = (0.572189m, 0.432457m, 0.418569m),
            ["100k to 200k"] = (0.574126m, 0.453004m, 0.425525m),
            ["200k to 350k"] = (0.655739m, 0.534233m, 0.523511m),
            ["350k to 600k"] = (0.690027m, 0.578948m, 0.562213m),
            ["600k to 900k"] = TopBandRatios, // was (0.452293m, 0.320548m, 0.295704m)
            ["1M and above"] = TopBandRatios, // was (0.708817m, 0.663222m, 0.636483m)
        },
        ["Pastes"] = new Dictionary<string, (decimal, decimal, decimal)>
        {
            ["up to 25,000"] = (0.377929m, 0.26819m,  0.226757m),
            ["25k to 50k"]   = (0.464615m, 0.306836m, 0.274957m),
            ["50k to 75k"]   = (0.49142m,  0.36409m,  0.335015m),
            ["75k to 100k"]  = (0.572189m, 0.432457m, 0.418569m),
            ["100k to 200k"] = (0.574126m, 0.453004m, 0.425525m),
            ["200k to 350k"] = (0.655739m, 0.534233m, 0.523511m),
            ["350k to 600k"] = (0.690027m, 0.578948m, 0.562213m),
            ["600k to 900k"] = TopBandRatios, // was (0.445547m, 0.304273m, 0.271382m)
            ["1M and above"] = TopBandRatios, // was (0.708817m, 0.663222m, 0.636483m)
        },
        ["Adhesives & Sealants"] = new Dictionary<string, (decimal, decimal, decimal)>
        {
            ["up to 25,000"] = (0.377929m, 0.26819m,  0.226757m),
            ["25k to 50k"]   = (0.464615m, 0.306836m, 0.274957m),
            ["50k to 75k"]   = (0.49142m,  0.36409m,  0.335015m),
            ["75k to 100k"]  = (0.572189m, 0.432457m, 0.418569m),
            ["100k to 200k"] = (0.574126m, 0.453004m, 0.425525m),
            ["200k to 350k"] = (0.655739m, 0.534233m, 0.523511m),
            ["350k to 600k"] = (0.690027m, 0.578948m, 0.562213m),
            ["600k to 900k"] = TopBandRatios, // was (0.445547m, 0.304273m, 0.271382m)
            ["1M and above"] = TopBandRatios, // was (0.708817m, 0.663222m, 0.636483m)
        },
        ["Repair Aids"] = new Dictionary<string, (decimal, decimal, decimal)>
        {
            ["up to 25,000"] = (0.377929m, 0.26819m,  0.226757m),
            ["25k to 50k"]   = (0.464615m, 0.306836m, 0.274957m),
            ["50k to 75k"]   = (0.49142m,  0.36409m,  0.335015m),
            ["75k to 100k"]  = (0.572189m, 0.432457m, 0.418569m),
            ["100k to 200k"] = (0.574126m, 0.453004m, 0.425525m),
            ["200k to 350k"] = (0.655739m, 0.534233m, 0.523511m),
            ["350k to 600k"] = (0.690027m, 0.578948m, 0.562213m),
            ["600k to 900k"] = TopBandRatios, // was (0.445547m, 0.304273m, 0.271382m)
            ["1M and above"] = TopBandRatios, // was (0.708817m, 0.663222m, 0.636483m)
        },
    };

    // ── PL04 "Maasai" ──────────────────────────────────────────────────────
    // Maasai price = PL03 (incl-VAT) × ratio, then ceil to 1 000, then ÷ VAT.
    // 4 bands on the incl-VAT Super-Dealer (PL03) output price:
    //   A: ≤  50 000    B: 50 001 – 200 000    C: 200 001 – 600 000    D: 600 001+
    // Ratios derived from 342-item Maasai pricing data (Aug 2026).
    // "Accessories" maps to "Service" (only 3 items; similar discount profile).

    private static readonly (decimal Lo, decimal Hi)[] MaasaiBandDefs =
    {
        (0m,        50_000m),
        (50_000m,  200_000m),
        (200_000m, 600_000m),
        (600_000m, decimal.MaxValue),
    };

    private static int GetMaasaiBand(decimal spInclVat)
    {
        for (int i = 0; i < MaasaiBandDefs.Length; i++)
            if (spInclVat >= MaasaiBandDefs[i].Lo && spInclVat < MaasaiBandDefs[i].Hi)
                return i;
        return MaasaiBandDefs.Length - 1;  // D band fallback
    }

    // [category] → { bandA, bandB, bandC, bandD } DEFAULT ratios (PL04 ÷ PL03)
    private static readonly IReadOnlyDictionary<string, decimal[]> DefaultMaasaiRatios
        = new Dictionary<string, decimal[]>
    {
        ["Additives"]                       = new[] { 0.6953m, 0.7121m, 0.6994m, 0.6994m },
        ["Adhesives & Sealants"]            = new[] { 0.6992m, 0.6992m, 0.6992m, 0.6992m },
        ["Engine Oils"]                     = new[] { 0.7432m, 0.7636m, 0.8051m, 0.8229m },
        ["Gear Oils & Transmission Fluids"] = new[] { 0.7190m, 0.7607m, 0.8033m, 0.7933m },
        ["Greases"]                         = new[] { 0.7598m, 0.7477m, 0.7842m, 0.7561m },
        ["Oils (Industrial/Other Fluids)"]  = new[] { 0.7692m, 0.7973m, 0.7979m, 0.7965m },
        ["Pastes"]                          = new[] { 0.6995m, 0.6995m, 0.6995m, 0.6995m },
        ["Repair Aids"]                     = new[] { 0.6752m, 0.6959m, 0.6856m, 0.6856m },
        ["Service"]                         = new[] { 0.7066m, 0.7381m, 0.7376m, 0.7134m },
        ["Vehicle Care"]                    = new[] { 0.7361m, 0.7277m, 0.7777m, 0.7371m },
        ["Workshop Pro-Line"]               = new[] { 0.6961m, 0.7167m, 0.7017m, 0.7017m },
    };

    // Maps the LM/scraped Category text to a canonical BandRatios key.
    private static readonly IReadOnlyDictionary<string, string> CategoryAliases
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["gear oils"]                        = "Gear Oils & Transmission Fluids",
        ["gear oils & transmission fluids"]  = "Gear Oils & Transmission Fluids",
        ["transmission fluids"]              = "Gear Oils & Transmission Fluids",
        ["engine oils"]                      = "Engine Oils",
        ["oils"]                             = "Engine Oils",   // safe default for ambiguous coarse "Oils"
        ["additives"]                        = "Additives",
        ["vehicle care"]                     = "Vehicle Care",
        ["service products"]                 = "Service",
        ["service"]                          = "Service",
        ["greases"]                          = "Greases",
        ["pastes"]                           = "Pastes",
        ["workshop pro-line"]                = "Workshop Pro-Line",
        ["adhesives & sealants"]             = "Adhesives & Sealants",
        ["repair aids"]                      = "Repair Aids",
        ["oils (industrial/other fluids)"]   = "Oils (Industrial/Other Fluids)",
        // Marine categories reuse their automotive equivalents' band ratios until
        // Marine-specific margins are defined.
        ["marine"]                           = "Engine Oils",
        ["marine oils"]                      = "Engine Oils",
        ["marine additives"]                 = "Additives",
    };

    // ── Runtime-editable effective tables ─────────────────────────────
    // Swapped atomically under _ratioLock; reads take a volatile-style local copy.
    private readonly object _ratioLock = new();
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, (decimal sp, decimal d, decimal r)>>
        _bandRatios = DefaultBandRatios;
    private IReadOnlyDictionary<string, decimal[]> _maasaiRatios = DefaultMaasaiRatios;
    private IReadOnlyList<BandRatioOverride> _bandOverrides = Array.Empty<BandRatioOverride>();
    private IReadOnlyList<MaasaiRatioOverride> _maasaiOverrides = Array.Empty<MaasaiRatioOverride>();

    private static readonly string[] MaasaiBandLabels =
    {
        "A: up to 50,000", "B: 50k to 200k", "C: 200k to 600k", "D: 600k+",
    };

    public PricingRatioSnapshot GetRatioSnapshot()
    {
        var bandRatios = _bandRatios;
        var maasai = _maasaiRatios;
        var bandOv = _bandOverrides.Select(o => (o.Category, o.Band)).ToHashSet();
        var maasaiOv = _maasaiOverrides.Select(o => (o.Category, o.BandIndex)).ToHashSet();

        var bandRows = new List<BandRatioRow>();
        foreach (var (cat, bands) in bandRatios)
            foreach (var (lo, hi, label) in BandDefs)
                if (bands.TryGetValue(label, out var r))
                    bandRows.Add(new BandRatioRow(cat, label, r.sp, r.d, r.r, bandOv.Contains((cat, label))));

        var maasaiRows = new List<MaasaiRatioRow>();
        foreach (var (cat, ratios) in maasai)
            for (int i = 0; i < ratios.Length; i++)
                maasaiRows.Add(new MaasaiRatioRow(cat, i, MaasaiBandLabels[i], ratios[i], maasaiOv.Contains((cat, i))));

        return new PricingRatioSnapshot(
            bandRatios.Keys.OrderBy(k => k).ToList(),
            BandDefs.Select(b => b.Label).ToList(),
            MaasaiBandLabels,
            bandRows,
            maasaiRows);
    }

    public void ApplyRatioOverrides(
        IReadOnlyList<BandRatioOverride> bandOverrides,
        IReadOnlyList<MaasaiRatioOverride> maasaiOverrides)
    {
        lock (_ratioLock)
        {
            _bandRatios = BuildBandTable(bandOverrides);
            _maasaiRatios = BuildMaasaiTable(maasaiOverrides);
            _bandOverrides = bandOverrides.ToList();
            _maasaiOverrides = maasaiOverrides.ToList();
        }
    }

    public PricingTrace Simulate(
        decimal cifCostTzs, string pricingCategory,
        BandRatioOverride? tempBand, MaasaiRatioOverride? tempMaasai)
    {
        var bandTable = _bandRatios;
        var maasaiTable = _maasaiRatios;

        if (tempBand is not null)
        {
            var merged = bandTable.ToDictionary(kv => kv.Key, kv => kv.Value);
            var catDict = merged.TryGetValue(tempBand.Category, out var existing)
                ? existing.ToDictionary(kv => kv.Key, kv => kv.Value)
                : new Dictionary<string, (decimal sp, decimal d, decimal r)>();
            catDict[tempBand.Band] = (tempBand.Sp, tempBand.Dealer, tempBand.Retail);
            merged[tempBand.Category] = catDict;
            bandTable = merged;
        }
        if (tempMaasai is not null && maasaiTable.TryGetValue(tempMaasai.Category, out var arr))
        {
            var merged = maasaiTable.ToDictionary(kv => kv.Key, kv => (decimal[])kv.Value.Clone());
            merged[tempMaasai.Category][Math.Clamp(tempMaasai.BandIndex, 0, 3)] = tempMaasai.Ratio;
            maasaiTable = merged;
        }

        return ComputeCapped(cifCostTzs, pricingCategory, bandTable, maasaiTable);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, (decimal sp, decimal d, decimal r)>>
        BuildBandTable(IReadOnlyList<BandRatioOverride> overrides)
    {
        var table = DefaultBandRatios.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToDictionary(b => b.Key, b => b.Value));
        foreach (var o in overrides)
        {
            if (!table.TryGetValue(o.Category, out var cat))
                table[o.Category] = cat = new Dictionary<string, (decimal, decimal, decimal)>();
            cat[o.Band] = (o.Sp, o.Dealer, o.Retail);
        }
        return table.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, (decimal sp, decimal d, decimal r)>)kv.Value);
    }

    private static IReadOnlyDictionary<string, decimal[]> BuildMaasaiTable(
        IReadOnlyList<MaasaiRatioOverride> overrides)
    {
        var table = DefaultMaasaiRatios.ToDictionary(kv => kv.Key, kv => (decimal[])kv.Value.Clone());
        foreach (var o in overrides)
        {
            if (!table.TryGetValue(o.Category, out var arr))
                table[o.Category] = arr = (decimal[])DefaultMaasaiRatios["Service"].Clone();
            arr[Math.Clamp(o.BandIndex, 0, 3)] = o.Ratio;
        }
        return table;
    }

    public string ResolvePricingCategory(string? scrapedCategory)
    {
        if (string.IsNullOrWhiteSpace(scrapedCategory))
            throw new InvalidOperationException("No category supplied for pricing.");
        var key = scrapedCategory.Trim();
        if (CategoryAliases.TryGetValue(key, out var mapped)) return mapped;
        if (_bandRatios.ContainsKey(key)) return key; // exact match

        // DGX returns hierarchical Odoo categories like "Service Products / Coolant / antifreeze" or
        // "Workshop Pro-Line / Petrol injector / system cleaners". The TOP-LEVEL segment is the pricing
        // band (Greases/Pastes/etc. are themselves top-level, so granularity is preserved). Walk the
        // segments parent-first and use the first that resolves to a band; an exact alias above can still
        // override any specific path.
        if (key.Contains('/'))
        {
            foreach (var segment in key.Split('/'))
            {
                var c = segment.Trim();
                if (CategoryAliases.TryGetValue(c, out mapped)) return mapped;
                if (_bandRatios.ContainsKey(c)) return c;
            }
        }

        throw new InvalidOperationException(
            $"No pricing category alias for '{scrapedCategory}'. Add it to CategoryAliases.");
    }

    // SAP/OITB item-group code -> pricing band. Pricing is keyed off the authoritative SAP group rather
    // than the (noisy, non-deterministic) Odoo category, so two products in the same group always price
    // the same. Codes verified against the live OITB. NOTE: 104 and 107 are COARSER than the pricing
    // bands (104 also covers "Repair Aids"; 107 also covers "Greases"/"Pastes") — the chosen band below
    // is the default for that group; confirm it matches intent.
    private static readonly IReadOnlyDictionary<int, string> SapGroupBand = new Dictionary<int, string>
    {
        [104] = "Service",                         // Repair aids/service products
        [105] = "Engine Oils",                     // Engine Oils
        [107] = "Gear Oils & Transmission Fluids", // Gear Oils/ATF/Greases
        [109] = "Additives",                       // Additives
        [111] = "Vehicle Care",                    // Vehicle Care
        [112] = "Workshop Pro-Line",               // Workshop Pro-Line
        [117] = "Greases",                         // Greases
        [118] = "Oils (Industrial/Other Fluids)",  // Oils (Industrial/Other Fluids)
        [119] = "Pastes",                          // Pastes
        [120] = "Service",                         // Service Products (same band as 104)
        [121] = "Adhesives & Sealants",            // Adhesives & Sealants
        // 106 Commercial vehicles/Construction and mining equipment, 108 Branding Materials, 110 Motor Bike:
        // no dedicated pricing band yet -> caller falls back to category-based resolution. Add here when
        // their bands are decided. Live OITB (Nov 2026): 104-112, 117-121.
    };

    public string? TryPricingBandForSapGroup(int sapGroupCode)
        => SapGroupBand.TryGetValue(sapGroupCode, out var band) ? band : null;

    // SAP groups shared by more than one LIQUI MOLY family. 107 "Gear Oils/ATF/Greases" is the old
    // combined group: greases belong in 117, but a grease still in 107 must not price as a gear oil.
    private const int SharedGearOilsGreasesGroup = 107;

    public string? TryPricingBandForSapGroup(int sapGroupCode, string? odooCategory)
    {
        if (sapGroupCode == SharedGearOilsGreasesGroup && IsGreasesCategory(odooCategory))
            return "Greases";
        return TryPricingBandForSapGroup(sapGroupCode);
    }

    /// <summary>True when an Odoo category (path, leaf name or lm_cat_ external id) is in the Greases family.</summary>
    internal static bool IsGreasesCategory(string? odooCategory)
    {
        if (string.IsNullOrWhiteSpace(odooCategory)) return false;
        var c = odooCategory.Trim();
        if (c.Contains("lm_cat_", StringComparison.OrdinalIgnoreCase))
            return c.Contains("lm_cat_greases", StringComparison.OrdinalIgnoreCase);
        var top = c.Split('/')[0].Trim();
        if (string.Equals(top, "Greases", StringComparison.OrdinalIgnoreCase)) return true;
        // A bare leaf name ("Multi-purpose grease") with no family path.
        return !c.Contains('/') && c.Contains("grease", StringComparison.OrdinalIgnoreCase);
    }

    public PricingBandResolution ResolveBandForItem(
        int? sapGroupCode, string? sapGroupName, string? odooCategoryName, string? odooCategoryExternalId)
    {
        var odooHint = !string.IsNullOrWhiteSpace(odooCategoryExternalId) ? odooCategoryExternalId : odooCategoryName;
        if (sapGroupCode is int grp && TryPricingBandForSapGroup(grp, odooHint) is string band)
        {
            var shared = band != TryPricingBandForSapGroup(grp)
                ? $" — group {grp} is shared, the Odoo category '{odooCategoryName ?? odooCategoryExternalId}' decides"
                : "";
            return new PricingBandResolution(band,
                $"SAP group {grp} ({sapGroupName ?? "?"}) → band '{band}' (authoritative){shared}");
        }
        if (!string.IsNullOrWhiteSpace(odooCategoryName))
        {
            try
            {
                var cat = ResolvePricingCategory(odooCategoryName);
                return new PricingBandResolution(cat,
                    $"SAP group {(sapGroupCode?.ToString() ?? "none")} has no band; "
                    + $"Odoo category '{odooCategoryName}' → band '{cat}' (fallback)");
            }
            catch (InvalidOperationException ex)
            {
                return new PricingBandResolution(null, ex.Message);
            }
        }
        return new PricingBandResolution(null,
            "No SAP group band and no Odoo category — cannot resolve a pricing band "
            + "(Accessories and other unmapped families are held for review).");
    }

    public PriceTiers ComputeNetPrices(decimal cifCostTzs, string pricingCategory)
        => ComputeNetPricesWithTrace(cifCostTzs, pricingCategory).Net;

    public PricingTrace ComputeNetPricesWithTrace(decimal cifCostTzs, string pricingCategory)
        => ComputeCapped(cifCostTzs, pricingCategory, _bandRatios, _maasaiRatios);

    /// <summary>One band's raw tier output at a given cost (incl-VAT, before the no-drop floor).</summary>
    private readonly record struct RawTiers(
        decimal Sp, decimal Dealer, decimal Retail, string Band, bool Adjusted,
        string Resolution, List<PricingTraceStep> Steps);

    /// <summary>Tier prices for one band, with the sp &lt; dealer &lt; retail ordering enforced.</summary>
    private static (decimal Sp, decimal Dealer, decimal Retail, bool Adjusted) TiersForBand(
        decimal cost, (decimal sp, decimal d, decimal r) r)
    {
        var sp     = Math.Ceiling(cost / r.sp / 1000m) * 1000m;
        var dealer = Math.Round  (cost / r.d  / 5000m, MidpointRounding.AwayFromZero) * 5000m;
        var retail = Math.Floor  (cost / r.r  / 5000m) * 5000m;
        bool adjusted = false;
        if (dealer >= retail) { dealer = retail - 5000m; adjusted = true; }
        if (sp >= dealer)     { sp     = dealer - 1000m; adjusted = true; }
        return (sp, dealer, retail, adjusted);
    }

    /// <summary>
    /// Band selection. Same walk as the HTML tool (seed "25k to 50k", recompute the band from the
    /// Super-Dealer price until it stops moving), but deterministic when it never settles: the HTML
    /// tool stopped after 5 passes and kept whichever band it happened to be on, so two bands that
    /// point at each other gave a price decided by the pass count. Here a repeated band ends the walk
    /// and, of the bands in the loop, the one giving the HIGHER price is used.
    /// </summary>
    private static RawTiers ResolveRaw(
        decimal cost, IReadOnlyDictionary<string, (decimal sp, decimal d, decimal r)> catRatios)
    {
        var band = "25k to 50k";   // seed band, matches the HTML tool
        var visited = new List<string>();
        var steps = new List<PricingTraceStep>();

        for (int i = 0; i <= BandDefs.Length; i++)
        {
            var r = catRatios[band];
            var t = TiersForBand(cost, r);
            var rawSp = Math.Ceiling(cost / r.sp / 1000m) * 1000m;
            steps.Add(new PricingTraceStep(i + 1, band, r.sp, r.d, r.r, t.Sp, t.Dealer, t.Retail));
            visited.Add(band);

            var next = GetBand(rawSp);
            if (next == band)
                return new RawTiers(t.Sp, t.Dealer, t.Retail, band, t.Adjusted, "converged", steps);

            var loopStart = visited.IndexOf(next);
            if (loopStart >= 0)
            {
                // Oscillation: pick the loop band with the highest price (Super Dealer, then Retail).
                var loop = visited.Skip(loopStart).Distinct().ToList();
                string best = loop[0];
                (decimal Sp, decimal Dealer, decimal Retail, bool Adjusted) bestT = TiersForBand(cost, catRatios[best]);
                foreach (var b in loop.Skip(1))
                {
                    var bt = TiersForBand(cost, catRatios[b]);
                    if (bt.Sp > bestT.Sp || (bt.Sp == bestT.Sp && bt.Retail > bestT.Retail))
                        (best, bestT) = (b, bt);
                }
                var note = $"oscillated between {string.Join(" and ", loop.Select(b => $"'{b}'"))}; "
                           + $"used '{best}' (the higher price)";
                return new RawTiers(bestT.Sp, bestT.Dealer, bestT.Retail, best, bestT.Adjusted, note, steps);
            }
            band = next;
        }
        // Unreachable: there are only BandDefs.Length bands, so a walk this long must repeat one.
        var last = TiersForBand(cost, catRatios[band]);
        return new RawTiers(last.Sp, last.Dealer, last.Retail, band, last.Adjusted, "converged", steps);
    }

    /// <summary>
    /// Costs at which the chosen band can change: band selection only depends on which band each
    /// band's Super-Dealer price falls in, and ceil(c / r / 1000) * 1000 reaches edge E exactly when
    /// c &gt; (E - 1000) * r. Between two of these costs the band is fixed and every tier only rises
    /// with cost, so the highest price at any lower cost is reached at one of these points.
    /// </summary>
    private static IEnumerable<decimal> BandChangeCosts(
        IReadOnlyDictionary<string, (decimal sp, decimal d, decimal r)> catRatios)
    {
        foreach (var r in catRatios.Values)
            foreach (var (lo, _, _) in BandDefs)
            {
                if (lo <= 0m) continue;
                var t = (lo - 1000m) * r.sp;
                yield return t;
                yield return t - 0.01m;   // guards decimal division landing a hair either side
            }
    }

    /// <summary>Maasai (PL04) incl-VAT from an incl-VAT Super-Dealer price, before the no-drop floor.</summary>
    private static decimal MaasaiRaw(decimal spInclVat, decimal[] ratios)
        => Math.Ceiling(spInclVat * ratios[GetMaasaiBand(spInclVat)] / 1000m) * 1000m;

    /// <summary>
    /// Maasai price that never falls as the Super-Dealer price rises: Maasai bands can have a lower
    /// ratio above an edge (e.g. Additives B 0.7121 → C 0.6994), so the price just below each edge is
    /// carried forward. Super-Dealer prices are whole thousands, so "just below" is edge − 1 000.
    /// </summary>
    private static decimal MaasaiFloored(decimal spInclVat, decimal[] ratios)
    {
        var m = MaasaiRaw(spInclVat, ratios);
        foreach (var (lo, _) in MaasaiBandDefs)
            if (lo > 0m && lo - 1000m > 0m && lo - 1000m <= spInclVat)
                m = Math.Max(m, MaasaiRaw(lo - 1000m, ratios));
        return m;
    }

    /// <summary>
    /// Band pricing up to the cap start; above it, the price at the cap start plus the extra cost
    /// marked up at the cap multiples (same rounding as the bands: SD up to 1 000, Dealer to the
    /// nearest 5 000, Retail down to 5 000). Maasai follows from the final Super Dealer price.
    /// </summary>
    private PricingTrace ComputeCapped(
        decimal cifCostTzs, string pricingCategory,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, (decimal sp, decimal d, decimal r)>> bandRatios,
        IReadOnlyDictionary<string, decimal[]> maasaiRatios)
    {
        if (cifCostTzs <= 0m)
            throw new ArgumentOutOfRangeException(nameof(cifCostTzs), "CIF cost must be > 0.");
        if (!_cap.Enabled || cifCostTzs <= _cap.StartCostTzs)
            return ApplyMaasaiFloor(ComputeCore(cifCostTzs, pricingCategory, bandRatios, maasaiRatios));

        var atStart = ComputeCore(_cap.StartCostTzs, pricingCategory, bandRatios, maasaiRatios);
        var extra = cifCostTzs - _cap.StartCostTzs;
        var sp     = Math.Ceiling((atStart.SuperDealerInclVat + extra * _cap.SuperDealer) / 1000m) * 1000m;
        var dealer = Math.Round  ((atStart.DealerInclVat      + extra * _cap.Dealer) / 5000m, MidpointRounding.AwayFromZero) * 5000m;
        var retail = Math.Floor  ((atStart.RetailInclVat      + extra * _cap.Retail) / 5000m) * 5000m;
        if (dealer >= retail) dealer = retail - 5000m;
        if (sp >= dealer)     sp     = dealer - 1000m;

        var maasaiCat = maasaiRatios.ContainsKey(pricingCategory) ? pricingCategory : "Service";
        var maasaiBand = GetMaasaiBand(sp);
        var maasai = MaasaiFloored(sp, maasaiRatios[maasaiCat]);

        var note = $"High-cost cap: the first {_cap.StartCostTzs:N0} TZS of cost is priced by the bands "
                   + $"(SD {atStart.SuperDealerInclVat:N0} / Dealer {atStart.DealerInclVat:N0} / Retail {atStart.RetailInclVat:N0}); "
                   + $"the remaining {extra:N0} TZS at SD {_cap.SuperDealer}x / Dealer {_cap.Dealer}x / Retail {_cap.Retail}x.";

        return ApplyMaasaiFloor(atStart with
        {
            CifCostTzs = cifCostTzs,
            RetailInclVat = retail,
            DealerInclVat = dealer,
            SuperDealerInclVat = sp,
            MaasaiCategory = maasaiCat,
            MaasaiBandIndex = maasaiBand,
            MaasaiRatio = maasaiRatios[maasaiCat][maasaiBand],
            MaasaiInclVat = maasai,
            Net = new PriceTiers(retail / VAT, dealer / VAT, sp / VAT, maasai / VAT),
            HasNonPositivePrice = sp <= 0m || dealer <= 0m || retail <= 0m || maasai <= 0m,
            CapNote = note,
        });
    }

    private static PricingTrace ComputeCore(
        decimal cifCostTzs, string pricingCategory,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, (decimal sp, decimal d, decimal r)>> bandRatios,
        IReadOnlyDictionary<string, decimal[]> maasaiRatios)
    {
        if (!bandRatios.TryGetValue(pricingCategory, out var catRatios))
            throw new InvalidOperationException($"Unknown pricing category '{pricingCategory}'.");

        var raw = ResolveRaw(cifCostTzs, catRatios);
        decimal sp = raw.Sp, dealer = raw.Dealer, retail = raw.Retail;

        // ── No-drop floor: a higher cost must never give a lower price. Each tier is lifted to the
        //    highest price any LOWER cost produces (at most a plateau until the formula catches up).
        //    The ordering sp < dealer < retail survives, as it holds at every point compared.
        foreach (var c in BandChangeCosts(catRatios))
        {
            if (c <= 0m || c >= cifCostTzs) continue;
            var lower = ResolveRaw(c, catRatios);
            if (lower.Sp > sp) sp = lower.Sp;
            if (lower.Dealer > dealer) dealer = lower.Dealer;
            if (lower.Retail > retail) retail = lower.Retail;
        }
        bool floored = sp != raw.Sp || dealer != raw.Dealer || retail != raw.Retail;

        // ── PL04 Maasai: derived from the final PL03 (sp) incl-VAT price. ──
        var maasaiCat = pricingCategory;
        if (!maasaiRatios.ContainsKey(maasaiCat))
            maasaiCat = "Service";   // fallback (covers Accessories + unmapped)
        int maasaiBand = GetMaasaiBand(sp);
        var maasaiRatio = maasaiRatios[maasaiCat][maasaiBand];
        var maasaiRawValue = MaasaiRaw(sp, maasaiRatios[maasaiCat]);
        var maasai = MaasaiFloored(sp, maasaiRatios[maasaiCat]);
        floored |= maasai != maasaiRawValue;

        string? floorNote = null;
        if (floored)
            floorNote = $"Lifted so a higher cost never prices lower. Formula at this cost gave "
                        + $"SD {raw.Sp:N0} / Dealer {raw.Dealer:N0} / Retail {raw.Retail:N0} / Maasai {maasaiRawValue:N0} (incl VAT).";

        // The HTML tool's rounded values are Incl-VAT (shelf prices).
        // SAP/Odoo price lists store NET (Excl-VAT) prices = Incl / 1.18.
        var net = new PriceTiers(
            Retail:      retail / VAT,
            Dealer:      dealer / VAT,
            SuperDealer: sp     / VAT,
            Maasai:      maasai / VAT);

        return new PricingTrace(
            CifCostTzs: cifCostTzs,
            PricingCategory: pricingCategory,
            Iterations: raw.Steps,
            FinalBand: raw.Band,
            OrderingAdjusted: raw.Adjusted,
            RetailInclVat: retail,
            DealerInclVat: dealer,
            SuperDealerInclVat: sp,
            MaasaiCategory: maasaiCat,
            MaasaiBandIndex: maasaiBand,
            MaasaiRatio: maasaiRatio,
            MaasaiInclVat: maasai,
            Net: net,
            BandResolution: raw.Resolution,
            MonotonicFloorApplied: floored,
            FloorNote: floorNote,
            HasNonPositivePrice: sp <= 0m || dealer <= 0m || retail <= 0m || maasai <= 0m);
    }

    public decimal ComputeMaasaiNetFromPl03Net(decimal pl03Net, string pricingCategory)
    {
        if (pl03Net <= 0m) return 0m;
        var sp = pl03Net * VAT;   // reconstruct incl-VAT PL03
        var cat = pricingCategory;
        if (!_maasaiRatios.ContainsKey(cat))
            cat = "Service";
        // PL03 net × 1.18 can land a few shillings off the whole thousand; snap back first.
        sp = Math.Round(sp / 1000m, MidpointRounding.AwayFromZero) * 1000m;
        var maasai = MaasaiFloored(sp, _maasaiRatios[cat]);
        return maasai / VAT;
    }
}
