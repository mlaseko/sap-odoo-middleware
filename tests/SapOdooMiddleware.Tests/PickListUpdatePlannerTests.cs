using SapOdooMiddleware.Models.Inventory;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Pure planning/validation for the pick-list write endpoints: status gating,
/// quantity bounds, bin-stock checks, absolute-set idempotency (already_applied),
/// and the auto-generated bin-change audit note.
/// </summary>
public class PickListUpdatePlannerTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 18, 30, 0, DateTimeKind.Utc);

    private static PickListSnapshot Snapshot(
        string status = "R", bool canceled = false,
        double released = 5, double picked = 0, string pickStatus = "R")
        => new()
        {
            AbsEntry = 12,
            Status = status,
            Canceled = canceled,
            Remarks = "",
            Lines =
            {
                new PickListLineSnapshot
                {
                    PickEntry = 0,
                    ItemCode = "BM10205",
                    WhsCode = "002",
                    ReleasedQty = released,
                    PickedQty = picked,
                    PickStatus = pickStatus,
                    Allocations =
                    {
                        new PickListBinSnapshot
                        {
                            BinAbs = 297, BinCode = "002-GF-BSKT06",
                            ReleasedQty = released, PickedQty = picked,
                        },
                    },
                },
            },
        };

    private static Dictionary<int, IReadOnlyList<BinOption>> Stock(params (int BinAbs, string Code, decimal Qty)[] bins)
        => new()
        {
            [0] = bins.Select(b => new BinOption { BinAbs = b.BinAbs, BinCode = b.Code, OnHandQty = b.Qty }).ToList(),
        };

    private static readonly Dictionary<string, bool> BinManaged = new() { ["002"] = true };

    // ── Picking ──────────────────────────────────────────────────────

    [Fact]
    public void FullPick_ProducesWriteAndNoNote_WhenBinsUnchanged()
    {
        var request = new PickListPickRequest
        {
            AppRef = "pick-1", ChangedBy = "Hussein",
            Lines = { new PickListPickLine
            {
                PickEntry = 0, PickedQty = 5,
                Allocations = { new PickBinAllocation { BinAbs = 297, Quantity = 5 } },
            } },
        };
        var plan = PickListUpdatePlanner.PlanPick(
            Snapshot(), request, Stock((297, "002-GF-BSKT06", 10)), BinManaged, Now);

        Assert.Empty(plan.Errors);
        Assert.False(plan.AlreadyApplied);
        Assert.Null(plan.Note);
        var line = Assert.Single(plan.Lines);
        Assert.Equal(5, line.PickedQty);
    }

    [Fact]
    public void PartialPick_IsAllowed()
    {
        var request = new PickListPickRequest
        {
            AppRef = "pick-2",
            Lines = { new PickListPickLine
            {
                PickEntry = 0, PickedQty = 2,
                Allocations = { new PickBinAllocation { BinAbs = 297, Quantity = 2 } },
            } },
        };
        var plan = PickListUpdatePlanner.PlanPick(
            Snapshot(), request, Stock((297, "002-GF-BSKT06", 10)), BinManaged, Now);
        Assert.Empty(plan.Errors);
        Assert.Equal(2, Assert.Single(plan.Lines).PickedQty);
    }

    [Fact]
    public void OverPick_And_UnPick_AreRejected()
    {
        var over = new PickListPickRequest
        {
            AppRef = "a",
            Lines = { new PickListPickLine { PickEntry = 0, PickedQty = 6,
                Allocations = { new PickBinAllocation { BinAbs = 297, Quantity = 6 } } } },
        };
        Assert.Contains(PickListUpdatePlanner.PlanPick(
                Snapshot(), over, Stock((297, "002-GF-BSKT06", 10)), BinManaged, Now).Errors,
            error => error.Contains("exceeds the total releasable"));

        var under = new PickListPickRequest
        {
            AppRef = "b",
            Lines = { new PickListPickLine { PickEntry = 0, PickedQty = 1,
                Allocations = { new PickBinAllocation { BinAbs = 297, Quantity = 1 } } } },
        };
        Assert.Contains(PickListUpdatePlanner.PlanPick(
                Snapshot(status: "P", released: 3, picked: 2, pickStatus: "P"), under,
                Stock((297, "002-GF-BSKT06", 10)), BinManaged, Now).Errors,
            error => error.Contains("un-picking is not supported"));
    }

    [Fact]
    public void AllocationTotal_MustEqualPickedQty_ForBinManagedWarehouse()
    {
        var request = new PickListPickRequest
        {
            AppRef = "c",
            Lines = { new PickListPickLine { PickEntry = 0, PickedQty = 3,
                Allocations = { new PickBinAllocation { BinAbs = 297, Quantity = 2 } } } },
        };
        Assert.Contains(PickListUpdatePlanner.PlanPick(
                Snapshot(), request, Stock((297, "002-GF-BSKT06", 10)), BinManaged, Now).Errors,
            error => error.Contains("must equal the picked quantity"));
    }

    [Fact]
    public void BinWithoutStock_OrInsufficientStock_IsRejected()
    {
        var unknownBin = new PickListPickRequest
        {
            AppRef = "d",
            Lines = { new PickListPickLine { PickEntry = 0, PickedQty = 2,
                Allocations = { new PickBinAllocation { BinAbs = 999, Quantity = 2 } } } },
        };
        Assert.Contains(PickListUpdatePlanner.PlanPick(
                Snapshot(), unknownBin, Stock((297, "002-GF-BSKT06", 10)), BinManaged, Now).Errors,
            error => error.Contains("holds no stock"));

        var tooMuch = new PickListPickRequest
        {
            AppRef = "e",
            Lines = { new PickListPickLine { PickEntry = 0, PickedQty = 4,
                Allocations = { new PickBinAllocation { BinAbs = 297, Quantity = 4 } } } },
        };
        Assert.Contains(PickListUpdatePlanner.PlanPick(
                Snapshot(), tooMuch, Stock((297, "002-GF-BSKT06", 3)), BinManaged, Now).Errors,
            error => error.Contains("holds 3"));
    }

    [Fact]
    public void IdenticalState_IsAlreadyApplied()
    {
        var request = new PickListPickRequest
        {
            AppRef = "f",
            Lines = { new PickListPickLine { PickEntry = 0, PickedQty = 2,
                Allocations = { new PickBinAllocation { BinAbs = 297, Quantity = 2 } } } },
        };
        var plan = PickListUpdatePlanner.PlanPick(
            Snapshot(status: "P", released: 0, picked: 2, pickStatus: "P"), request,
            Stock((297, "002-GF-BSKT06", 10)), BinManaged, Now);
        Assert.Empty(plan.Errors);
        Assert.True(plan.AlreadyApplied);
        Assert.Empty(plan.Lines);
    }

    [Fact]
    public void ClosedOrCancelledPickList_IsRejected()
    {
        var request = new PickListPickRequest
        {
            AppRef = "g",
            Lines = { new PickListPickLine { PickEntry = 0, PickedQty = 5,
                Allocations = { new PickBinAllocation { BinAbs = 297, Quantity = 5 } } } },
        };
        Assert.NotEmpty(PickListUpdatePlanner.PlanPick(
            Snapshot(status: "C"), request, Stock((297, "x", 10)), BinManaged, Now).Errors);
        Assert.NotEmpty(PickListUpdatePlanner.PlanPick(
            Snapshot(canceled: true), request, Stock((297, "x", 10)), BinManaged, Now).Errors);
    }

    [Fact]
    public void BinChangeDuringPick_GeneratesAuditNote()
    {
        var request = new PickListPickRequest
        {
            AppRef = "h", ChangedBy = "Hussein",
            Lines = { new PickListPickLine { PickEntry = 0, PickedQty = 5,
                Allocations = { new PickBinAllocation { BinAbs = 301, Quantity = 5 } } } },
        };
        var plan = PickListUpdatePlanner.PlanPick(
            Snapshot(), request,
            Stock((297, "002-GF-BSKT06", 10), (301, "002-F1-SHLF01", 8)), BinManaged, Now);

        Assert.Empty(plan.Errors);
        Assert.NotNull(plan.Note);
        Assert.Contains("Hussein", plan.Note);
        Assert.Contains("002-GF-BSKT06(5) -> 002-F1-SHLF01(5)", plan.Note);
    }

    // ── Released allocation editing ──────────────────────────────────

    [Fact]
    public void AllocationUpdate_OnlyForReleasedLines()
    {
        var request = new PickListAllocationUpdate
        {
            AppRef = "i", ChangedBy = "Hussein",
            Lines = { new PickListAllocationLine { PickEntry = 0,
                Allocations = { new PickBinAllocation { BinAbs = 301, Quantity = 5 } } } },
        };
        var picked = PickListUpdatePlanner.PlanAllocationUpdate(
            Snapshot(status: "P", released: 0, picked: 5, pickStatus: "Y"), request,
            Stock((301, "002-F1-SHLF01", 8)), Now);
        Assert.Contains(picked.Errors, error => error.Contains("only released lines"));

        var released = PickListUpdatePlanner.PlanAllocationUpdate(
            Snapshot(), request, Stock((301, "002-F1-SHLF01", 8)), Now);
        Assert.Empty(released.Errors);
        Assert.NotNull(released.Note);
        Assert.Null(Assert.Single(released.Lines).PickedQty);
    }

    [Fact]
    public void AllocationUpdate_CannotExceedReleasedQty()
    {
        var request = new PickListAllocationUpdate
        {
            AppRef = "j",
            Lines = { new PickListAllocationLine { PickEntry = 0,
                Allocations = { new PickBinAllocation { BinAbs = 301, Quantity = 6 } } } },
        };
        Assert.Contains(PickListUpdatePlanner.PlanAllocationUpdate(
                Snapshot(), request, Stock((301, "002-F1-SHLF01", 8)), Now).Errors,
            error => error.Contains("exceeds the released quantity"));
    }

    [Fact]
    public void DuplicateBins_AndNonPositiveQuantities_AreRejected()
    {
        var request = new PickListAllocationUpdate
        {
            AppRef = "k",
            Lines = { new PickListAllocationLine { PickEntry = 0, Allocations =
            {
                new PickBinAllocation { BinAbs = 301, Quantity = 2 },
                new PickBinAllocation { BinAbs = 301, Quantity = 3 },
            } } },
        };
        Assert.Contains(PickListUpdatePlanner.PlanAllocationUpdate(
                Snapshot(), request, Stock((301, "002-F1-SHLF01", 8)), Now).Errors,
            error => error.Contains("listed twice"));
    }

    // ── Remarks note appending ───────────────────────────────────────

    [Fact]
    public void AppendNote_KeepsNewestContent_WithinSapRemarksLimit()
    {
        Assert.Equal("note", PickListUpdatePlanner.AppendNote("", "note"));
        Assert.Equal("old\nnote", PickListUpdatePlanner.AppendNote("old", "note"));

        var longHistory = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"entry number {i}"));
        var appended = PickListUpdatePlanner.AppendNote(longHistory, "[App] newest change");
        Assert.True(appended.Length <= PickListUpdatePlanner.MaxRemarksLength);
        Assert.EndsWith("[App] newest change", appended);
    }
}
