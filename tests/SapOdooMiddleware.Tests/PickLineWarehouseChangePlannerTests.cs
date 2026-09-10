using SapOdooMiddleware.Models.Inventory;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Pure validation for re-sourcing a released pick-list line to another
/// warehouse: line/warehouse existence, the picked-quantity guard, the
/// sales-order linkage requirement, and already_applied semantics.
/// </summary>
public class PickLineWarehouseChangePlannerTests
{
    private static readonly string[] Warehouses = { "001", "002", "003" };

    private static PickListSnapshot Snapshot(
        string pickStatus = "R", double pickedQty = 0, bool canceled = false, int orderEntry = 28685)
        => new()
        {
            AbsEntry = 35,
            Status = "R",
            Canceled = canceled,
            Lines =
            {
                new PickListLineSnapshot
                {
                    PickEntry = 0, OrderEntry = orderEntry, OrderLine = 0,
                    ItemCode = "VAG12818", WhsCode = "003",
                    ReleasedQty = 1, PickedQty = pickedQty, PickStatus = pickStatus,
                },
            },
        };

    private static PickLineWarehouseChange Request(string whs = "001", int pickEntry = 0)
        => new() { AppRef = "resource-test", ChangedBy = "Mohamed", PickEntry = pickEntry, WhsCode = whs };

    [Fact]
    public void HappyPathCarriesTheSalesOrderLinkageAndTargetWarehouse()
    {
        var plan = PickLineWarehouseChangePlanner.Plan(Snapshot(), Request(), Warehouses);
        Assert.Empty(plan.Errors);
        Assert.False(plan.AlreadyApplied);
        Assert.Equal(28685, plan.OrderEntry);
        Assert.Equal(0, plan.OrderLine);
        Assert.Equal("003", plan.SourceWhs);
        Assert.Equal("001", plan.TargetWhs);
    }

    [Fact]
    public void RejectsUnknownWarehousesLinesAndCancelledLists()
    {
        Assert.Contains(
            PickLineWarehouseChangePlanner.Plan(Snapshot(), Request(whs: "099"), Warehouses).Errors,
            e => e.Contains("not found"));
        Assert.Contains(
            PickLineWarehouseChangePlanner.Plan(Snapshot(), Request(whs: " "), Warehouses).Errors,
            e => e.Contains("required"));
        Assert.Contains(
            PickLineWarehouseChangePlanner.Plan(Snapshot(), Request(pickEntry: 7), Warehouses).Errors,
            e => e.Contains("no line 7"));
        Assert.Contains(
            PickLineWarehouseChangePlanner.Plan(Snapshot(canceled: true), Request(), Warehouses).Errors,
            e => e.Contains("cancelled"));
    }

    [Fact]
    public void PickedOrClosedLinesAreProtected()
    {
        // Any picked quantity means a picker's work would be invalidated.
        Assert.Contains(
            PickLineWarehouseChangePlanner.Plan(Snapshot(pickedQty: 1), Request(), Warehouses).Errors,
            e => e.Contains("picked"));
        Assert.Contains(
            PickLineWarehouseChangePlanner.Plan(Snapshot(pickStatus: "P"), Request(), Warehouses).Errors,
            e => e.Contains("picked"));
        Assert.Contains(
            PickLineWarehouseChangePlanner.Plan(Snapshot(pickStatus: "C"), Request(), Warehouses).Errors,
            e => e.Contains("closed"));
    }

    [Fact]
    public void LinesWithoutASalesOrderReferenceCannotBeResourced()
    {
        Assert.Contains(
            PickLineWarehouseChangePlanner.Plan(Snapshot(orderEntry: 0), Request(), Warehouses).Errors,
            e => e.Contains("sales-order reference"));
    }

    [Fact]
    public void RequestingTheCurrentWarehouseIsAlreadyApplied()
    {
        var plan = PickLineWarehouseChangePlanner.Plan(Snapshot(), Request(whs: "003"), Warehouses);
        Assert.Empty(plan.Errors);
        Assert.True(plan.AlreadyApplied);
    }
}
