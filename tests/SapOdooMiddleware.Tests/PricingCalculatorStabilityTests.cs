using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Pricing;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Nov 2026 pricing fixes: deterministic band choice when the band walk never settles, the no-drop
/// floor (a higher cost never prices lower), non-positive guard, the shared-group-107 greases rule,
/// warn-only margins, the preview pin on Confirm &amp; Post, and the formula impact report.
/// </summary>
public class PricingCalculatorStabilityTests
{
    private readonly PricingCalculator _calc = new();

    public static IEnumerable<object[]> Categories() =>
        PricingCalculatorCategoryList.All.Select(c => new object[] { c });

    // ── Band choice ─────────────────────────────────────────────────

    [Fact]
    public void EngineOils_40000_oscillating_band_uses_the_higher_price_every_time()
    {
        // 25k-50k → 74k (band 50-75k) → 82k (band 75-100k) → 70k (band 50-75k) → ... never settles.
        // The HTML tool kept pass 5 (75-100k: 70k / 90k / 95k); the fix uses the higher loop band.
        var t = _calc.ComputeNetPricesWithTrace(40_000m, "Engine Oils");

        Assert.Equal(82_000m, t.SuperDealerInclVat);
        Assert.Equal(110_000m, t.DealerInclVat);
        Assert.Equal(115_000m, t.RetailInclVat);
        Assert.Equal("50k to 75k", t.FinalBand);   // the band whose ratios produced the prices
        Assert.Contains("oscillated", t.BandResolution);
        Assert.Equal(t.SuperDealerInclVat, _calc.ComputeNetPricesWithTrace(40_000m, "Engine Oils").SuperDealerInclVat);
    }

    [Fact]
    public void Converged_cost_without_a_drop_is_unchanged_from_the_html_tool()
    {
        // Engine Oils at 100,000: settles in band 100k-200k; no lower cost prices higher.
        var t = _calc.ComputeNetPricesWithTrace(100_000m, "Engine Oils");

        Assert.Equal("converged", t.BandResolution);
        Assert.False(t.MonotonicFloorApplied);
        Assert.Equal(169_000m, t.SuperDealerInclVat);
        Assert.Equal(210_000m, t.DealerInclVat);
        Assert.Equal(225_000m, t.RetailInclVat);
        Assert.Equal("100k to 200k", t.FinalBand);
    }

    [Fact]
    public void No_drop_at_the_old_1M_band_edge()
    {
        // The HTML tool: 637,000 → Retail 1,485,000 but 637,500 → Retail 1,000,000.
        var below = _calc.ComputeNetPricesWithTrace(637_000m, "Engine Oils");
        var above = _calc.ComputeNetPricesWithTrace(637_500m, "Engine Oils");

        Assert.True(above.RetailInclVat >= below.RetailInclVat);
        Assert.True(above.DealerInclVat >= below.DealerInclVat);
        Assert.True(above.SuperDealerInclVat >= below.SuperDealerInclVat);
        Assert.True(above.MaasaiInclVat >= below.MaasaiInclVat);
    }

    [Theory]
    [MemberData(nameof(Categories))]
    public void Every_tier_never_drops_as_cost_rises_and_stays_ordered(string category)
    {
        PricingTrace? prev = null;
        for (var cost = 5_000m; cost <= 5_000_000m; cost += cost < 700_000m ? 500m : 5_000m)
        {
            var t = _calc.ComputeNetPricesWithTrace(cost, category);

            Assert.True(t.SuperDealerInclVat < t.DealerInclVat, $"{category} @ {cost}: SD < Dealer");
            Assert.True(t.DealerInclVat < t.RetailInclVat, $"{category} @ {cost}: Dealer < Retail");
            Assert.True(t.MaasaiInclVat < t.SuperDealerInclVat, $"{category} @ {cost}: Maasai < SD");

            if (prev is not null)
            {
                Assert.True(t.SuperDealerInclVat >= prev.SuperDealerInclVat, $"{category}: SD drops at {cost}");
                Assert.True(t.DealerInclVat >= prev.DealerInclVat, $"{category}: Dealer drops at {cost}");
                Assert.True(t.RetailInclVat >= prev.RetailInclVat, $"{category}: Retail drops at {cost}");
                Assert.True(t.MaasaiInclVat >= prev.MaasaiInclVat, $"{category}: Maasai drops at {cost}");
            }
            prev = t;
        }
    }

    [Theory]
    [MemberData(nameof(Categories))]
    public void Top_bands_use_the_agreed_markups_for_every_category(string category)
    {
        var rows = _calc.GetRatioSnapshot().BandRatios.Where(r => r.Category == category).ToList();
        foreach (var band in new[] { "600k to 900k", "1M and above" })
        {
            var r = rows.Single(x => x.Band == band);
            Assert.Equal(1.85m, Math.Round(1m / r.Sp, 4));
            Assert.Equal(2.00m, Math.Round(1m / r.Dealer, 4));
            Assert.Equal(2.10m, Math.Round(1m / r.Retail, 4));
        }
    }

    [Theory]
    // Engine Oils at 3,150 TZS/EUR: EUR 400 (cap start), EUR 600, EUR 1,000 — option 5b simulation.
    [InlineData(1_260_000, 2_645_000, 2_520_000, 2_332_000)]
    [InlineData(1_890_000, 3_525_000, 3_370_000, 3_139_000)]
    [InlineData(3_150_000, 5_290_000, 5_070_000, 4_752_000)]
    public void High_cost_cap_matches_the_option_5b_simulation(decimal cost, decimal retail, decimal dealer, decimal sd)
    {
        var t = _calc.ComputeNetPricesWithTrace(cost, "Engine Oils");
        Assert.Equal(retail, t.RetailInclVat);
        Assert.Equal(dealer, t.DealerInclVat);
        Assert.Equal(sd, t.SuperDealerInclVat);
        Assert.Equal(cost > 1_260_000m, t.CapNote is not null);
    }

    [Fact]
    public void Cap_keeps_profit_rising_while_the_markup_tapers()
    {
        decimal prevProfit = 0m, prevMultiple = decimal.MaxValue;
        for (var eur = 400m; eur <= 1_500m; eur += 50m)
        {
            var cost = eur * 3_150m;
            var t = _calc.ComputeNetPricesWithTrace(cost, "Engine Oils");
            var profit = t.Net.Retail - cost;
            var multiple = t.RetailInclVat / cost;
            Assert.True(profit > prevProfit, $"retail profit must rise at EUR {eur}");
            Assert.True(multiple <= prevMultiple + 0.005m, $"markup must not rise at EUR {eur}");
            (prevProfit, prevMultiple) = (profit, multiple);
        }
    }

    [Fact]
    public void Cap_can_be_disabled_and_rejects_unordered_multiples()
    {
        var noCap = new PricingCalculator(new PricingCap(StartCostTzs: 0m));
        Assert.Null(noCap.ComputeNetPricesWithTrace(3_150_000m, "Engine Oils").CapNote);
        Assert.Throws<ArgumentException>(() => new PricingCalculator(new PricingCap(1_000_000m, 1.30m, 1.35m, 1.28m)));
        Assert.Throws<ArgumentException>(() => new PricingCalculator(new PricingCap(1_000_000m, 1.40m, 1.35m, 1.10m)));
    }

    [Fact]
    public void Tiny_cost_is_flagged_non_positive()
    {
        var t = _calc.ComputeNetPricesWithTrace(1_000m, "Engine Oils");
        Assert.True(t.HasNonPositivePrice);
        Assert.False(_calc.ComputeNetPricesWithTrace(20_000m, "Engine Oils").HasNonPositivePrice);
    }

    [Fact]
    public void Maasai_from_PL03_never_drops_across_maasai_band_edges()
    {
        // Additives Maasai ratio falls B 0.7121 → C 0.6994 at 200k incl VAT.
        var below = _calc.ComputeMaasaiNetFromPl03Net(199_000m / 1.18m, "Additives");
        var above = _calc.ComputeMaasaiNetFromPl03Net(200_000m / 1.18m, "Additives");
        Assert.True(above >= below);
    }

    // ── Shared group 107 (Gear Oils/ATF/Greases) ────────────────────

    [Theory]
    [InlineData("Greases / Multi-purpose grease", "Greases")]
    [InlineData("__import__.lm_cat_greases__spray_grease", "Greases")]
    [InlineData("Multi-purpose grease", "Greases")]
    [InlineData("Gear Oils & Transmission Fluids / Gear oils", "Gear Oils & Transmission Fluids")]
    [InlineData("__import__.lm_cat_gear_oils_transmission_fluids__gear_oils", "Gear Oils & Transmission Fluids")]
    [InlineData(null, "Gear Oils & Transmission Fluids")]
    public void Group_107_follows_the_Odoo_category_for_greases(string? odooCategory, string expected)
        => Assert.Equal(expected, _calc.TryPricingBandForSapGroup(107, odooCategory));

    [Fact]
    public void Greases_rule_only_applies_to_group_107()
    {
        Assert.Equal("Engine Oils", _calc.TryPricingBandForSapGroup(105, "Greases / Multi-purpose grease"));
        Assert.Equal("Greases", _calc.TryPricingBandForSapGroup(117, null));
    }

    [Fact]
    public void ResolveBandForItem_matches_the_agreed_rules()
    {
        Assert.Equal("Service", _calc.ResolveBandForItem(104, "Repair aids", "Repair Aids / Leak detection", null).Band);
        Assert.Equal("Engine Oils",
            _calc.ResolveBandForItem(106, "Commercial vehicles", "Engine Oils / Commercial vehicle engine oils", null).Band);
        Assert.Equal("Greases",
            _calc.ResolveBandForItem(107, "Gear Oils/ATF/Greases", "Greases / High-temperature grease", null).Band);

        // Accessories: no SAP group and no band → held for review.
        var acc = _calc.ResolveBandForItem(null, null, "Accessories / Workshop accessories", null);
        Assert.Null(acc.Band);
        Assert.False(string.IsNullOrWhiteSpace(acc.Path));
    }

    // ── Reprice helpers ─────────────────────────────────────────────

    [Fact]
    public void MarginWarnings_flags_below_cost_and_thin_margins_only()
    {
        var net = new Dictionary<int, decimal> { [1] = 200m, [2] = 150m, [3] = 103m, [4] = 95m };
        var w = LubesRepriceService.MarginWarnings(net, 100m, 5m).ToList();

        Assert.Equal(2, w.Count);
        Assert.Contains(w, x => x.StartsWith("PL3") && x.Contains("only 3%"));
        Assert.Contains(w, x => x.StartsWith("PL4") && x.Contains("BELOW"));
    }

    [Fact]
    public void PreviewMismatch_detects_changed_prices_and_ignores_missing_pin()
    {
        var now = new Dictionary<int, decimal> { [1] = 100m, [2] = 90m, [3] = 80m, [4] = 60m };

        Assert.Null(LubesRepriceService.PreviewMismatch(null, now));
        Assert.Null(LubesRepriceService.PreviewMismatch(new Dictionary<int, decimal>(now), now));
        var diff = LubesRepriceService.PreviewMismatch(
            new Dictionary<int, decimal> { [1] = 100m, [2] = 95m, [3] = 80m, [4] = 60m }, now);
        Assert.NotNull(diff);
        Assert.Contains("PL2", diff);
    }

    // ── Impact report ───────────────────────────────────────────────

    private static Dictionary<int, decimal> NetOf(PricingTrace t) => new()
    {
        [1] = Math.Round(t.Net.Retail, 2), [2] = Math.Round(t.Net.Dealer, 2),
        [3] = Math.Round(t.Net.SuperDealer, 2), [4] = Math.Round(t.Net.Maasai, 2),
    };

    [Fact]
    public void Impact_item_priced_by_the_current_formula_shows_no_change()
    {
        var stored = NetOf(_calc.ComputeNetPricesWithTrace(50m * 2950m, "Engine Oils"));
        var src = new PricingImpactSource("1234", "Oil", 105, "Engine Oils", "Engine Oils / Passenger car engine oils",
            null, 50m, 2950m, stored);

        var row = LubesPricingImpactService.Analyze(_calc, src, useStoredRate: true, currentRate: 3100m);

        Assert.False(row.PricesChange);
        Assert.Null(row.Error);
        Assert.Equal(2950m, row.Rate);   // stored rate isolates the formula
    }

    [Fact]
    public void Impact_flags_a_grease_in_group_107_and_its_price_change()
    {
        var asGearOil = NetOf(_calc.ComputeNetPricesWithTrace(9m * 2950m, "Gear Oils & Transmission Fluids"));
        var src = new PricingImpactSource("3400", "Grease", 107, "Gear Oils/ATF/Greases",
            "Greases / Multi-purpose grease", "__import__.lm_cat_greases__multi_purpose_grease", 9m, 2950m, asGearOil);

        var row = LubesPricingImpactService.Analyze(_calc, src, useStoredRate: true, currentRate: 2950m);

        Assert.True(row.GreaseInGroup107);
        Assert.Equal("Greases", row.PricingBand);
        Assert.Contains(row.Reasons, r => r.Contains("117"));
    }

    [Fact]
    public void Impact_reports_items_without_a_cost_or_band_as_errors()
    {
        var noCost = LubesPricingImpactService.Analyze(_calc,
            new PricingImpactSource("1", null, 107, null, "Greases / Spray grease", null, null, null, new()),
            true, 2950m);
        Assert.True(noCost.GreaseInGroup107);
        Assert.NotNull(noCost.Error);

        var noBand = LubesPricingImpactService.Analyze(_calc,
            new PricingImpactSource("2", null, null, null, "Accessories / Workshop accessories", null, 10m, 2950m, new()),
            true, 2950m);
        Assert.NotNull(noBand.Error);
    }
}

internal static class PricingCalculatorCategoryList
{
    public static readonly string[] All =
    {
        "Engine Oils", "Additives", "Gear Oils & Transmission Fluids", "Greases",
        "Oils (Industrial/Other Fluids)", "Service", "Vehicle Care",
        "Workshop Pro-Line", "Pastes", "Adhesives & Sealants", "Repair Aids",
    };
}
