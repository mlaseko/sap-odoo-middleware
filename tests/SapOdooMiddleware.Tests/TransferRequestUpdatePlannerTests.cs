using SapOdooMiddleware.Models.Inventory;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Pure planning/validation for the transfer-request update/close endpoints:
/// document/line status gating, the fulfilled-quantity floor, absolute-quantity
/// idempotency (already_applied), added lines, and comments handling.
/// </summary>
public class TransferRequestUpdatePlannerTests
{
    private static TransferRequestSnapshot Snapshot(
        string docStatus = "O", string canceled = "N",
        double quantity = 10, double openQty = 10, string lineStatus = "O")
        => new()
        {
            DocEntry = 41,
            DocNum = 315,
            DocDate = "2026-09-01",
            FromWhs = "001",
            ToWhs = "002",
            DocStatus = docStatus,
            Canceled = canceled,
            Comments = "urgent restock",
            Lines =
            {
                new TransferRequestSnapshotLine
                {
                    LineNum = 0, ItemCode = "BM10205", ItemName = "Center Bearing",
                    Quantity = quantity, OpenQty = openQty, LineStatus = lineStatus,
                },
                new TransferRequestSnapshotLine
                {
                    LineNum = 1, ItemCode = "VAG10575", ItemName = "Brake Pad Set",
                    Quantity = 4, OpenQty = 4, LineStatus = "O",
                },
            },
        };

    private static TransferRequestUpdate QtyUpdate(int lineNum, double quantity)
        => new() { Lines = { new TransferRequestLineQuantityUpdate { LineNum = lineNum, Quantity = quantity } } };

    [Fact]
    public void RejectsClosedAndCancelledDocuments()
    {
        var closedPlan = TransferRequestUpdatePlanner.PlanUpdate(Snapshot(docStatus: "C"), QtyUpdate(0, 5));
        Assert.Contains(closedPlan.Errors, e => e.Contains("closed"));

        var cancelledPlan = TransferRequestUpdatePlanner.PlanUpdate(Snapshot(canceled: "Y"), QtyUpdate(0, 5));
        Assert.Contains(cancelledPlan.Errors, e => e.Contains("cancelled"));
    }

    [Fact]
    public void RejectsAnEmptyUpdate()
    {
        var plan = TransferRequestUpdatePlanner.PlanUpdate(Snapshot(), new TransferRequestUpdate());
        Assert.Contains(plan.Errors, e => e.Contains("Nothing to update"));
    }

    [Fact]
    public void RejectsUnknownDuplicateAndClosedLines()
    {
        var unknown = TransferRequestUpdatePlanner.PlanUpdate(Snapshot(), QtyUpdate(7, 5));
        Assert.Contains(unknown.Errors, e => e.Contains("no line 7"));

        var duplicate = new TransferRequestUpdate
        {
            Lines =
            {
                new TransferRequestLineQuantityUpdate { LineNum = 0, Quantity = 5 },
                new TransferRequestLineQuantityUpdate { LineNum = 0, Quantity = 6 },
            },
        };
        Assert.Contains(
            TransferRequestUpdatePlanner.PlanUpdate(Snapshot(), duplicate).Errors,
            e => e.Contains("listed twice"));

        var closedLine = TransferRequestUpdatePlanner.PlanUpdate(
            Snapshot(lineStatus: "C"), QtyUpdate(0, 5));
        Assert.Contains(closedLine.Errors, e => e.Contains("closed"));
    }

    [Fact]
    public void EnforcesTheFulfilledQuantityFloor()
    {
        // 10 requested, 3 open → 7 already transferred; 5 would cut below that.
        var plan = TransferRequestUpdatePlanner.PlanUpdate(
            Snapshot(quantity: 10, openQty: 3), QtyUpdate(0, 5));
        Assert.Contains(plan.Errors, e => e.Contains("already been"));

        // Exactly the fulfilled amount is allowed — that is how a line is closed.
        var toFulfilled = TransferRequestUpdatePlanner.PlanUpdate(
            Snapshot(quantity: 10, openQty: 3), QtyUpdate(0, 7));
        Assert.Empty(toFulfilled.Errors);
        Assert.Single(toFulfilled.QuantityWrites);

        var zero = TransferRequestUpdatePlanner.PlanUpdate(Snapshot(), QtyUpdate(0, 0));
        Assert.Contains(zero.Errors, e => e.Contains("greater than zero"));
    }

    [Fact]
    public void UnchangedQuantitiesAreAlreadyApplied()
    {
        var plan = TransferRequestUpdatePlanner.PlanUpdate(Snapshot(quantity: 10), QtyUpdate(0, 10));
        Assert.Empty(plan.Errors);
        Assert.True(plan.AlreadyApplied);
        Assert.Empty(plan.QuantityWrites);
    }

    [Fact]
    public void ValidatesAndPlansAddedLines()
    {
        var invalid = new TransferRequestUpdate
        {
            AddLines =
            {
                new TransferRequestLineCreate { ItemCode = " ", Quantity = 1 },
                new TransferRequestLineCreate { ItemCode = "TY20031", Quantity = 0 },
            },
        };
        var invalidPlan = TransferRequestUpdatePlanner.PlanUpdate(Snapshot(), invalid);
        Assert.Contains(invalidPlan.Errors, e => e.Contains("item_code is required"));
        Assert.Contains(invalidPlan.Errors, e => e.Contains("greater than zero"));

        var valid = new TransferRequestUpdate
        {
            AddLines = { new TransferRequestLineCreate { ItemCode = " TY20031 ", Quantity = 2 } },
        };
        var plan = TransferRequestUpdatePlanner.PlanUpdate(Snapshot(), valid);
        Assert.Empty(plan.Errors);
        Assert.False(plan.AlreadyApplied);
        Assert.Equal("TY20031", Assert.Single(plan.NewLines).ItemCode);
    }

    [Fact]
    public void CommentsAreTrimmedComparedAndLengthChecked()
    {
        var unchanged = TransferRequestUpdatePlanner.PlanUpdate(
            Snapshot(), new TransferRequestUpdate { Comments = "  urgent restock  " });
        Assert.Empty(unchanged.Errors);
        Assert.True(unchanged.AlreadyApplied);

        var changed = TransferRequestUpdatePlanner.PlanUpdate(
            Snapshot(), new TransferRequestUpdate { Comments = "now for branch 002" });
        Assert.Empty(changed.Errors);
        Assert.Equal("now for branch 002", changed.Comments);

        var tooLong = TransferRequestUpdatePlanner.PlanUpdate(
            Snapshot(), new TransferRequestUpdate { Comments = new string('x', 300) });
        Assert.Contains(tooLong.Errors, e => e.Contains("254"));
    }

    [Fact]
    public void MixedValidUpdateProducesOnlyTheChangedWrites()
    {
        var request = new TransferRequestUpdate
        {
            Lines =
            {
                new TransferRequestLineQuantityUpdate { LineNum = 0, Quantity = 10 }, // unchanged
                new TransferRequestLineQuantityUpdate { LineNum = 1, Quantity = 6 },  // 4 → 6
            },
            AddLines = { new TransferRequestLineCreate { ItemCode = "TY20031", Quantity = 1 } },
        };
        var plan = TransferRequestUpdatePlanner.PlanUpdate(Snapshot(), request);
        Assert.Empty(plan.Errors);
        Assert.False(plan.AlreadyApplied);
        var write = Assert.Single(plan.QuantityWrites);
        Assert.Equal(1, write.LineNum);
        Assert.Equal(6, write.Quantity);
        Assert.Single(plan.NewLines);
    }

    [Fact]
    public void RemoveDeletesUnfulfilledAndClosesPartiallyFulfilledLines()
    {
        // Line 0 has 7 of 10 fulfilled; line 1 is untouched.
        var request = new TransferRequestUpdate { RemoveLines = { 0, 1 }, AddLines = { new TransferRequestLineCreate { ItemCode = "TY20031", Quantity = 2 } } };
        var plan = TransferRequestUpdatePlanner.PlanUpdate(Snapshot(quantity: 10, openQty: 3), request);
        Assert.Empty(plan.Errors);
        // Partially fulfilled line 0 → closed by reducing to the fulfilled amount.
        var write = Assert.Single(plan.QuantityWrites);
        Assert.Equal(0, write.LineNum);
        Assert.Equal(7, write.Quantity);
        // Unfulfilled line 1 → deleted outright.
        Assert.Equal(1, Assert.Single(plan.DeleteLineNums));
    }

    [Fact]
    public void RemoveValidatesLineExistenceStatusAndOverlap()
    {
        var unknown = TransferRequestUpdatePlanner.PlanUpdate(
            Snapshot(), new TransferRequestUpdate { RemoveLines = { 7 } });
        Assert.Contains(unknown.Errors, e => e.Contains("no line 7"));

        var closed = TransferRequestUpdatePlanner.PlanUpdate(
            Snapshot(lineStatus: "C"), new TransferRequestUpdate { RemoveLines = { 0 } });
        Assert.Contains(closed.Errors, e => e.Contains("already closed"));

        var overlap = new TransferRequestUpdate
        {
            RemoveLines = { 1 },
            Lines = { new TransferRequestLineQuantityUpdate { LineNum = 1, Quantity = 6 } },
        };
        Assert.Contains(
            TransferRequestUpdatePlanner.PlanUpdate(Snapshot(), overlap).Errors,
            e => e.Contains("pick one"));
    }

    [Fact]
    public void RemovingEveryOpenLineRequiresTheCloseEndpoint()
    {
        var all = TransferRequestUpdatePlanner.PlanUpdate(
            Snapshot(), new TransferRequestUpdate { RemoveLines = { 0, 1 } });
        Assert.Contains(all.Errors, e => e.Contains("close endpoint"));

        // Removing one of two open lines is fine.
        var one = TransferRequestUpdatePlanner.PlanUpdate(
            Snapshot(), new TransferRequestUpdate { RemoveLines = { 1 } });
        Assert.Empty(one.Errors);
        Assert.Equal(1, Assert.Single(one.DeleteLineNums));

        // Removing every open line while adding a replacement is also fine.
        var withAdd = TransferRequestUpdatePlanner.PlanUpdate(
            Snapshot(),
            new TransferRequestUpdate
            {
                RemoveLines = { 0, 1 },
                AddLines = { new TransferRequestLineCreate { ItemCode = "TY20031", Quantity = 1 } },
            });
        Assert.Empty(withAdd.Errors);
        Assert.Equal(2, withAdd.DeleteLineNums.Count);
    }

    [Fact]
    public void CloseIsIdempotentOnClosedOrCancelledDocuments()
    {
        Assert.Empty(TransferRequestUpdatePlanner.ValidateClose(Snapshot(), out bool openAlready));
        Assert.False(openAlready);

        TransferRequestUpdatePlanner.ValidateClose(Snapshot(docStatus: "C"), out bool closedAlready);
        Assert.True(closedAlready);

        TransferRequestUpdatePlanner.ValidateClose(Snapshot(canceled: "Y"), out bool cancelledAlready);
        Assert.True(cancelledAlready);
    }
}
