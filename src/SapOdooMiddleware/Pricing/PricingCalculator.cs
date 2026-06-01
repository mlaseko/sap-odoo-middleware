namespace SapOdooMiddleware.Pricing;

/// <summary>Three net (excl-VAT) prices in TZS.</summary>
public record PriceTiers(decimal Retail, decimal Dealer, decimal SuperDealer);

public interface IPricingCalculator
{
    /// <summary>Compute Retail/Dealer/Super-Dealer NET (excl-VAT) prices from CIF cost in TZS.</summary>
    PriceTiers ComputeNetPrices(decimal cifCostTzs, string pricingCategory);

    /// <summary>Resolve a scraped/LM category string to a canonical pricing-category key.</summary>
    string ResolvePricingCategory(string? scrapedCategory);
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
/// </summary>
public class PricingCalculator : IPricingCalculator
{
    private const decimal VAT = 1.18m;

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

    // (sp, d, r) ratios. Cost = price × ratio, so price = cost / ratio.
    private static readonly IReadOnlyDictionary<string,
        IReadOnlyDictionary<string, (decimal sp, decimal d, decimal r)>> BandRatios
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
            ["600k to 900k"] = (0.442655m, 0.30166m,  0.265692m),
            ["1M and above"] = (0.708817m, 0.663222m, 0.636483m),
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
            ["600k to 900k"] = (0.5862m,   0.464878m, 0.428126m),
            ["1M and above"] = (0.708817m, 0.663222m, 0.636483m),
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
            ["600k to 900k"] = (0.57413m,  0.426881m, 0.401149m),
            ["1M and above"] = (0.708817m, 0.663222m, 0.636483m),
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
            ["600k to 900k"] = (0.593563m, 0.390056m, 0.341299m),
            ["1M and above"] = (0.708817m, 0.663222m, 0.636483m),
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
            ["600k to 900k"] = (0.655489m, 0.501236m, 0.478683m),
            ["1M and above"] = (0.708817m, 0.663222m, 0.636483m),
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
            ["600k to 900k"] = (0.445547m, 0.304273m, 0.271382m),
            ["1M and above"] = (0.708817m, 0.663222m, 0.636483m),
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
            ["600k to 900k"] = (0.538114m, 0.332442m, 0.31608m),
            ["1M and above"] = (0.708817m, 0.663222m, 0.636483m),
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
            ["600k to 900k"] = (0.452293m, 0.320548m, 0.295704m),
            ["1M and above"] = (0.708817m, 0.663222m, 0.636483m),
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
            ["600k to 900k"] = (0.445547m, 0.304273m, 0.271382m),
            ["1M and above"] = (0.708817m, 0.663222m, 0.636483m),
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
            ["600k to 900k"] = (0.445547m, 0.304273m, 0.271382m),
            ["1M and above"] = (0.708817m, 0.663222m, 0.636483m),
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
            ["600k to 900k"] = (0.445547m, 0.304273m, 0.271382m),
            ["1M and above"] = (0.708817m, 0.663222m, 0.636483m),
        },
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

    public string ResolvePricingCategory(string? scrapedCategory)
    {
        if (string.IsNullOrWhiteSpace(scrapedCategory))
            throw new InvalidOperationException("No category supplied for pricing.");
        var key = scrapedCategory.Trim();
        if (CategoryAliases.TryGetValue(key, out var mapped)) return mapped;
        if (BandRatios.ContainsKey(key)) return key; // exact match
        throw new InvalidOperationException(
            $"No pricing category alias for '{scrapedCategory}'. Add it to CategoryAliases.");
    }

    public PriceTiers ComputeNetPrices(decimal cifCostTzs, string pricingCategory)
    {
        if (cifCostTzs <= 0m)
            throw new ArgumentOutOfRangeException(nameof(cifCostTzs), "CIF cost must be > 0.");
        if (!BandRatios.TryGetValue(pricingCategory, out var catRatios))
            throw new InvalidOperationException($"Unknown pricing category '{pricingCategory}'.");

        var band = "25k to 50k";   // seed band, matches the HTML tool
        decimal sp = 0, dealer = 0, retail = 0;

        for (int i = 0; i < 5; i++)
        {
            var r = catRatios[band];
            sp     = Math.Ceiling(cifCostTzs / r.sp / 1000m) * 1000m;
            dealer = Math.Round  (cifCostTzs / r.d  / 5000m, MidpointRounding.AwayFromZero) * 5000m;
            retail = Math.Floor  (cifCostTzs / r.r  / 5000m) * 5000m;
            var nb = GetBand(sp);
            if (nb == band) break;
            band = nb;
        }

        if (dealer >= retail) dealer = retail - 5000m;
        if (sp >= dealer)     sp     = dealer - 1000m;

        // The HTML tool's rounded values are Incl-VAT (shelf prices).
        // SAP/Odoo price lists store NET (Excl-VAT) prices = Incl / 1.18.
        return new PriceTiers(
            Retail:      retail / VAT,
            Dealer:      dealer / VAT,
            SuperDealer: sp     / VAT);
    }
}
