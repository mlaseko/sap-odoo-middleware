using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

public class BinAllocationPlannerTests
{
    private const string COCWHSE = "COCWHSE";
    private const string MAINWHSE = "MAINWHSE";

    private const string BIN_1ST = "COCWHSE-SHOW ROOM 1ST FLOOR";
    private const string BIN_GF = "COCWHSE-SHOW ROOM GROUND FLOOR";
    private const string BIN_MAIN_SYS = "MAINWHSE-SYSTEM-BIN-LOCATION";
    private const string BIN_MAIN_RCV = "MAINWHSE-RCV";

    private static readonly IReadOnlyList<string> Priority = new[]
    {
        BIN_1ST, BIN_GF, BIN_MAIN_SYS, BIN_MAIN_RCV,
    };

    private static BinAllocationPlanner.BinStock Bin(
        int absEntry, string binCode, string whsCode,
        params (string item, double qty)[] stock) =>
        new(
            absEntry, binCode, whsCode,
            stock.ToDictionary(s => s.item, s => s.qty, StringComparer.OrdinalIgnoreCase));

    // -------- happy path --------

    [Fact]
    public void Plan_allocates_from_highest_priority_bin_with_stock_and_locks_that_warehouse()
    {
        var binInfo = new Dictionary<string, BinAllocationPlanner.BinStock>(StringComparer.OrdinalIgnoreCase)
        {
            [BIN_1ST] = Bin(1, BIN_1ST, COCWHSE, ("1598", 70)),
            [BIN_MAIN_SYS] = Bin(3, BIN_MAIN_SYS, MAINWHSE, ("1598", 13252)),
        };
        var lines = new[]
        {
            new BinAllocationPlanner.LineInput(0, "1598", 1, null),
        };

        var plans = BinAllocationPlanner.Plan(
            lines, Priority, binInfo, allowFallback: true, defaultWarehouseCode: MAINWHSE);

        Assert.Single(plans);
        var p = plans[0];
        Assert.Equal(COCWHSE, p.WhsCode);           // locked onto 1ST FLOOR's warehouse
        Assert.Equal(0, p.Shortfall);
        Assert.Single(p.Picks);
        Assert.Equal(BIN_1ST, p.Picks[0].BinCode);  // took from the highest-priority bin
        Assert.Equal(1.0, p.Picks[0].Qty);
        Assert.False(p.Picks[0].IsFallback);
    }

    [Fact]
    public void Plan_walks_priority_bins_in_configured_order_within_locked_warehouse()
    {
        // 1ST FLOOR has only 2 units; GROUND FLOOR has 10.  Line needs 5.
        var binInfo = new Dictionary<string, BinAllocationPlanner.BinStock>(StringComparer.OrdinalIgnoreCase)
        {
            [BIN_1ST] = Bin(1, BIN_1ST, COCWHSE, ("A", 2)),
            [BIN_GF]  = Bin(2, BIN_GF,  COCWHSE, ("A", 10)),
        };

        var plans = BinAllocationPlanner.Plan(
            new[] { new BinAllocationPlanner.LineInput(0, "A", 5, null) },
            Priority, binInfo, allowFallback: false, defaultWarehouseCode: MAINWHSE);

        var p = plans[0];
        Assert.Equal(COCWHSE, p.WhsCode);
        Assert.Equal(0, p.Shortfall);
        Assert.Equal(2, p.Picks.Count);
        Assert.Equal(BIN_1ST, p.Picks[0].BinCode);
        Assert.Equal(2.0, p.Picks[0].Qty);
        Assert.Equal(BIN_GF, p.Picks[1].BinCode);
        Assert.Equal(3.0, p.Picks[1].Qty);
    }

    // -------- Choice X: keep line whole in one warehouse --------

    [Fact]
    public void Plan_does_not_cross_warehouses_even_when_remaining_warehouse_has_plenty()
    {
        // Line needs 100.  COCWHSE has 70 total (1ST FLOOR 30 + GROUND FLOOR 40).
        // MAINWHSE has 500 in priority bin.  We deliberately stay in COCWHSE.
        var binInfo = new Dictionary<string, BinAllocationPlanner.BinStock>(StringComparer.OrdinalIgnoreCase)
        {
            [BIN_1ST] = Bin(1, BIN_1ST, COCWHSE, ("A", 30)),
            [BIN_GF]  = Bin(2, BIN_GF,  COCWHSE, ("A", 40)),
            [BIN_MAIN_SYS] = Bin(3, BIN_MAIN_SYS, MAINWHSE, ("A", 500)),
        };

        var plans = BinAllocationPlanner.Plan(
            new[] { new BinAllocationPlanner.LineInput(0, "A", 100, null) },
            Priority, binInfo, allowFallback: true, defaultWarehouseCode: MAINWHSE);

        var p = plans[0];
        Assert.Equal(COCWHSE, p.WhsCode);                                  // locked to COCWHSE
        Assert.Equal(30, p.Shortfall);                                     // 100 - 70
        Assert.All(p.Picks, bp => Assert.Equal(COCWHSE, bp.WhsCode));      // no MAINWHSE picks
        Assert.Equal(70, p.Picks.Sum(bp => bp.Qty));
    }

    // -------- fallback --------

    [Fact]
    public void Plan_uses_fallback_non_priority_bins_only_when_allowFallback_true()
    {
        // Item A stock lives in an unregistered bin in COCWHSE.
        var binInfo = new Dictionary<string, BinAllocationPlanner.BinStock>(StringComparer.OrdinalIgnoreCase)
        {
            ["COCWHSE-OTHER"] = Bin(9, "COCWHSE-OTHER", COCWHSE, ("A", 20)),
        };

        // With fallback OFF: no allocation, falls back to default warehouse.
        var withoutFallback = BinAllocationPlanner.Plan(
            new[] { new BinAllocationPlanner.LineInput(0, "A", 5, null) },
            Priority, binInfo, allowFallback: false, defaultWarehouseCode: MAINWHSE);
        Assert.Equal(MAINWHSE, withoutFallback[0].WhsCode);
        Assert.Equal(5, withoutFallback[0].Shortfall);
        Assert.Empty(withoutFallback[0].Picks);

        // With fallback ON: locked onto COCWHSE, satisfied from the non-priority bin.
        var withFallback = BinAllocationPlanner.Plan(
            new[] { new BinAllocationPlanner.LineInput(0, "A", 5, null) },
            Priority, binInfo, allowFallback: true, defaultWarehouseCode: MAINWHSE);
        Assert.Equal(COCWHSE, withFallback[0].WhsCode);
        Assert.Equal(0, withFallback[0].Shortfall);
        Assert.Single(withFallback[0].Picks);
        Assert.True(withFallback[0].Picks[0].IsFallback);
    }

    [Fact]
    public void Plan_fallback_bins_are_chosen_largest_first_within_locked_warehouse()
    {
        var binInfo = new Dictionary<string, BinAllocationPlanner.BinStock>(StringComparer.OrdinalIgnoreCase)
        {
            ["COCWHSE-SMALL"] = Bin(8, "COCWHSE-SMALL", COCWHSE, ("A", 3)),
            ["COCWHSE-BIG"]   = Bin(9, "COCWHSE-BIG",   COCWHSE, ("A", 100)),
        };

        var plans = BinAllocationPlanner.Plan(
            new[] { new BinAllocationPlanner.LineInput(0, "A", 50, null) },
            Priority, binInfo, allowFallback: true, defaultWarehouseCode: MAINWHSE);

        var p = plans[0];
        Assert.Equal(COCWHSE, p.WhsCode);
        Assert.Equal(0, p.Shortfall);
        Assert.Single(p.Picks);
        Assert.Equal("COCWHSE-BIG", p.Picks[0].BinCode);
        Assert.Equal(50, p.Picks[0].Qty);
    }

    // -------- decrement semantics --------

    [Fact]
    public void Plan_decrements_stock_so_second_line_does_not_over_allocate_same_bin()
    {
        var binInfo = new Dictionary<string, BinAllocationPlanner.BinStock>(StringComparer.OrdinalIgnoreCase)
        {
            [BIN_1ST] = Bin(1, BIN_1ST, COCWHSE, ("A", 10)),
        };

        var plans = BinAllocationPlanner.Plan(
            new[]
            {
                new BinAllocationPlanner.LineInput(0, "A", 7, null),
                new BinAllocationPlanner.LineInput(1, "A", 5, null),
            },
            Priority, binInfo, allowFallback: false, defaultWarehouseCode: MAINWHSE);

        Assert.Equal(0, plans[0].Shortfall);
        Assert.Equal(7, plans[0].Picks.Single().Qty);
        Assert.Equal(2, plans[1].Shortfall);                 // only 3 left after line 0
        Assert.Equal(3, plans[1].Picks.Single().Qty);
    }

    [Fact]
    public void Plan_does_not_mutate_caller_binInfo()
    {
        var binInfo = new Dictionary<string, BinAllocationPlanner.BinStock>(StringComparer.OrdinalIgnoreCase)
        {
            [BIN_1ST] = Bin(1, BIN_1ST, COCWHSE, ("A", 10)),
        };

        BinAllocationPlanner.Plan(
            new[] { new BinAllocationPlanner.LineInput(0, "A", 10, null) },
            Priority, binInfo, allowFallback: false, defaultWarehouseCode: MAINWHSE);

        Assert.Equal(10, binInfo[BIN_1ST].OnHandByItemCode["A"]);
    }

    // -------- zero stock / shortfall --------

    [Fact]
    public void Plan_defaults_to_DefaultWarehouseCode_when_no_stock_anywhere()
    {
        var binInfo = new Dictionary<string, BinAllocationPlanner.BinStock>(StringComparer.OrdinalIgnoreCase);

        var plans = BinAllocationPlanner.Plan(
            new[] { new BinAllocationPlanner.LineInput(0, "A", 5, null) },
            Priority, binInfo, allowFallback: true, defaultWarehouseCode: MAINWHSE);

        Assert.Equal(MAINWHSE, plans[0].WhsCode);
        Assert.Equal(5, plans[0].Shortfall);
        Assert.Empty(plans[0].Picks);
    }

    // -------- explicit WhsCode (Phase 0) --------

    [Fact]
    public void Plan_honours_explicit_WhsCode_on_line_and_only_uses_bins_in_that_warehouse()
    {
        var binInfo = new Dictionary<string, BinAllocationPlanner.BinStock>(StringComparer.OrdinalIgnoreCase)
        {
            [BIN_1ST]      = Bin(1, BIN_1ST,      COCWHSE,  ("A", 100)),
            [BIN_MAIN_SYS] = Bin(3, BIN_MAIN_SYS, MAINWHSE, ("A", 100)),
        };

        // Caller pins MAINWHSE; we respect it even though 1ST FLOOR has plenty.
        var plans = BinAllocationPlanner.Plan(
            new[] { new BinAllocationPlanner.LineInput(0, "A", 5, MAINWHSE) },
            Priority, binInfo, allowFallback: false, defaultWarehouseCode: MAINWHSE);

        var p = plans[0];
        Assert.Equal(MAINWHSE, p.WhsCode);
        Assert.Equal(0, p.Shortfall);
        Assert.Single(p.Picks);
        Assert.Equal(BIN_MAIN_SYS, p.Picks[0].BinCode);
    }

    // -------- regression: the S09868 scenario --------

    [Fact]
    public void Regression_S09868_cascades_to_COCWHSE_when_MAINWHSE_has_no_stock()
    {
        // Exactly what the production log showed for item 1598: 70 units
        // in a COCWHSE bin, 0 in every MAINWHSE bin. Without the planner
        // the SO line went to MAINWHSE (via SAP default) and the pick
        // list skipped the line.
        var binInfo = new Dictionary<string, BinAllocationPlanner.BinStock>(StringComparer.OrdinalIgnoreCase)
        {
            [BIN_1ST] = Bin(1, BIN_1ST, COCWHSE, ("1598", 70)),
            [BIN_GF]  = Bin(2, BIN_GF,  COCWHSE, ("1598", 0)),   // present in cache, empty
        };

        var plans = BinAllocationPlanner.Plan(
            new[] { new BinAllocationPlanner.LineInput(0, "1598", 1, null) },
            Priority, binInfo, allowFallback: true, defaultWarehouseCode: MAINWHSE);

        var p = plans[0];
        Assert.Equal(COCWHSE, p.WhsCode);
        Assert.Equal(0, p.Shortfall);
        Assert.Single(p.Picks);
        Assert.Equal(BIN_1ST, p.Picks[0].BinCode);
    }
}
