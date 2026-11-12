using ClosedXML.Excel;
using SapOdooMiddleware.Pricing;

namespace SapOdooMiddleware.Tests;

/// <summary>Maasai cost floor and the price simulator (pure pricing + Excel in/out).</summary>
public class PriceSimulatorTests
{
    private readonly PricingCalculator _calc = new();

    // ── Maasai floor ────────────────────────────────────────────────

    [Theory]
    [InlineData("Engine Oils", 4_725_000)]   // EUR 1,500 at 3,150: was about break-even
    [InlineData("Additives", 3_150_000)]     // EUR 1,000: was about 10% below cost
    [InlineData("Greases", 2_263_149)]       // Meguin LP3 grease
    public void Maasai_is_never_below_cost_plus_the_floor(string category, decimal cost)
    {
        var t = _calc.ComputeNetPricesWithTrace(cost, category);
        Assert.True(t.Net.Maasai >= cost * 1.05m, $"Maasai net {t.Net.Maasai:N0} < cost + 5% ({cost * 1.05m:N0})");
        Assert.True(t.MaasaiInclVat < t.SuperDealerInclVat);
    }

    [Fact]
    public void Maasai_floor_reports_itself_and_can_be_turned_off()
    {
        var on = _calc.ComputeNetPricesWithTrace(3_150_000m, "Additives");
        Assert.NotNull(on.MaasaiFloorNote);

        var off = new PricingCalculator(new PricingCap(), maasaiMinMarginPct: -1m)
            .ComputeNetPricesWithTrace(3_150_000m, "Additives");
        Assert.Null(off.MaasaiFloorNote);
        Assert.True(off.MaasaiInclVat < on.MaasaiInclVat);
    }

    [Fact]
    public void Maasai_floor_leaves_ordinary_items_alone()
    {
        // A typical 5 L engine oil (EUR 30): the ratio already gives far more than cost + 5%.
        var t = _calc.ComputeNetPricesWithTrace(30m * 3_150m, "Engine Oils");
        Assert.Null(t.MaasaiFloorNote);
    }

    // ── Simulator ───────────────────────────────────────────────────

    [Fact]
    public void Simulate_matches_the_calculator_for_an_EUR_cost()
    {
        var r = LubesPriceSimulator.Simulate(_calc,
            new SimulationInput("NEW-1", "Oil 205 L", 1000m, "EUR", "Engine Oils"), 3150m, 5m);
        var t = _calc.ComputeNetPricesWithTrace(3_150_000m, "Engine Oils");

        Assert.True(r.Ok);
        Assert.Equal(3_150_000m, r.CostTzs);
        Assert.Equal(t.RetailInclVat, r.InclVat![1]);
        Assert.Equal(t.MaasaiInclVat, r.InclVat[4]);
        Assert.Equal(Math.Round(t.RetailInclVat / 1.18m, 2), r.Net![1]);
        Assert.Contains(r.Notes, n => n.Contains("cap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Simulate_takes_a_TZS_cost_as_is()
    {
        var r = LubesPriceSimulator.Simulate(_calc,
            new SimulationInput(null, null, 100_000m, "tzs", "Additives"), 3150m, 5m);
        Assert.True(r.Ok);
        Assert.Equal(100_000m, r.CostTzs);
    }

    [Fact]
    public void Simulate_resolves_SAP_group_107_greases_and_rejects_missing_or_bad_input()
    {
        var grease = LubesPriceSimulator.Simulate(_calc,
            new SimulationInput(null, null, 9m, "EUR", null, 107, "Greases / Multi-purpose grease"), 3150m, 5m);
        Assert.Equal("Greases", grease.Band);

        Assert.NotNull(LubesPriceSimulator.Simulate(_calc, new SimulationInput(null, null, 9m), 3150m, 5m).Error);
        Assert.NotNull(LubesPriceSimulator.Simulate(_calc, new SimulationInput(null, null, 9m, "EUR", "Nonsense"), 3150m, 5m).Error);
        Assert.NotNull(LubesPriceSimulator.Simulate(_calc, new SimulationInput(null, null, 0m, "EUR", "Engine Oils"), 3150m, 5m).Error);
        Assert.NotNull(LubesPriceSimulator.Simulate(_calc, new SimulationInput(null, null, 9m, "USD", "Engine Oils"), 3150m, 5m).Error);
        Assert.NotNull(LubesPriceSimulator.Simulate(_calc,
            new SimulationInput(null, null, 9m, "EUR", null, null, "Accessories / Workshop accessories"), 3150m, 5m).Error);
    }

    [Fact]
    public void Simulate_accepts_band_aliases()
    {
        var r = LubesPriceSimulator.Simulate(_calc,
            new SimulationInput(null, null, 12m, "EUR", "Service Products"), 3150m, 5m);
        Assert.Equal("Service", r.Band);
    }

    // ── Excel ───────────────────────────────────────────────────────

    [Fact]
    public void Excel_upload_reads_named_columns_in_any_order_and_reports_bad_rows()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("x");
        string[] h = { "Description", "Cost", "Band", "Reference", "Currency", "SapGroup", "OdooCategory", "Benchmark" };
        for (int i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i];
        ws.Cell(2, 1).Value = "Oil"; ws.Cell(2, 2).Value = 32.5; ws.Cell(2, 3).Value = "Engine Oils"; ws.Cell(2, 4).Value = "A1";
        ws.Cell(3, 1).Value = "Grease"; ws.Cell(3, 2).Value = "1,250"; ws.Cell(3, 5).Value = "TZS"; ws.Cell(3, 6).Value = 117;
        ws.Cell(4, 1).Value = "Bad"; ws.Cell(4, 2).Value = "abc";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var (rows, errors) = SimulatorExcel.Parse(ms);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new SimulationInput("A1", "Oil", 32.5m, "EUR", "Engine Oils"), rows[0]);
        Assert.Equal(1250m, rows[1].Cost);
        Assert.Equal("TZS", rows[1].Currency);
        Assert.Equal(117, rows[1].SapGroup);
        Assert.Single(errors);
        Assert.Contains("Row 4", errors[0]);
    }

    [Fact]
    public void Excel_results_and_template_open_and_round_trip()
    {
        var results = new[]
        {
            LubesPriceSimulator.Simulate(_calc, new SimulationInput("A1", "Oil", 32.5m, "EUR", "Engine Oils"), 3150m, 5m),
            LubesPriceSimulator.Simulate(_calc, new SimulationInput("B2", "Bad", 9m, "EUR", "Nonsense"), 3150m, 5m),
        };
        using (var wb = new XLWorkbook(new MemoryStream(SimulatorExcel.Write(results))))
        {
            var ws = wb.Worksheet(1);
            Assert.Equal("A1", ws.Cell(2, 1).GetString());
            Assert.Equal((double)results[0].InclVat![1], ws.Cell(2, 8).GetDouble());
            Assert.False(string.IsNullOrEmpty(ws.Cell(3, 23).GetString()));   // error column for the bad row
        }

        var (rows, errors) = SimulatorExcel.Parse(new MemoryStream(SimulatorExcel.Template()));
        Assert.Equal(2, rows.Count);
        Assert.Empty(errors);
    }
}
