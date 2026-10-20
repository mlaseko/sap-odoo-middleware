using SapOdooMiddleware.Models.PurchaseSync;
using SapOdooMiddleware.Services.PurchaseSync;

namespace SapOdooMiddleware.Tests.PurchaseSync;

public class LandedCostPlanningTests
{
    private static readonly string[] Codes = { "C02", "C08", "EXC" };

    private static readonly LandedCostGrpo Grpo = new(345, 1345, "S00001", Cancelled: false, ShipRef: "MSKU1234567");

    private static readonly List<SapBaseLine> GrpoLines = new()
    {
        new SapBaseLine(0, "LM-1001", 120, "01"),
        new SapBaseLine(1, "LM-1002", 40, "01"),
    };

    private static LandedCostPushRequest Request(params (string Code, double Amount)[] lines) => new()
    {
        OdooOrderId = 12,
        OdooShipmentId = "SO-2026-0012",
        ShipRef = "MSKU1234567",
        GrpoDocentry = 345,
        CostLines = lines.Select(l => new LandedCostLineRequest { LcCode = l.Code, AmountTzs = l.Amount }).ToList(),
    };

    [Theory]
    [InlineData("SO-2026-0012", 1, "SO-2026-0012")]
    [InlineData("SO-2026-0012", 0, "SO-2026-0012")]
    [InlineData("SO-2026-0012", 2, "SO-2026-0012:2")]
    [InlineData(" SO-2026-0012 ", 3, "SO-2026-0012:3")]
    public void IdempotencyKey_IsTheReferenceThenRefColonSequence(string reference, int sequence, string expected)
    {
        Assert.Equal(expected, LandedCostPlanning.IdempotencyKey(reference, sequence));
    }

    [Fact]
    public void IdempotencyKey_KeepsTheSuffixWhenClippedTo50()
    {
        string key = LandedCostPlanning.IdempotencyKey(new string('X', 60), 4);
        Assert.Equal(50, key.Length);
        Assert.EndsWith(":4", key);
    }

    [Theory]
    [InlineData("SO-2026-0012", true)]
    [InlineData("so-2026-0012", true)]
    [InlineData("SO-2026-0012:2", true)]
    [InlineData("SO-2026-0012:x", false)]
    [InlineData("SO-2026-0013", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void BelongsTo_AcceptsTheReferenceAndItsDeltas(string? udf, bool expected)
    {
        Assert.Equal(expected, LandedCostPlanning.BelongsTo(udf, "SO-2026-0012"));
    }

    [Fact]
    public void Validate_ReportsEveryShapeProblem()
    {
        var request = new LandedCostPushRequest
        {
            OdooShipmentId = " ", GrpoDocentry = 0, PushSequence = 2, PreviousLcDocentry = null,
            CostLines = new List<LandedCostLineRequest> { new() { LcCode = "", AmountTzs = 1, AllocationBy = "by-mood" } },
        };

        var errors = LandedCostPlanning.Validate(request);

        Assert.Contains(errors, e => e.Contains("odoo_shipment_id"));
        Assert.Contains(errors, e => e.Contains("grpo_docentry"));
        Assert.Contains(errors, e => e.Contains("previous_lc_docentry is required"));
        Assert.Contains(errors, e => e.Contains("cost_lines[0].lc_code"));
        Assert.Contains(errors, e => e.Contains("allocation_by"));
    }

    [Fact]
    public void Validate_FirstPushWithPreviousDocument_IsAnError_AndCleanRequestIsNot()
    {
        var request = Request(("C02", 1250000));
        Assert.Empty(LandedCostPlanning.Validate(request));

        request.PreviousLcDocentry = 77;
        Assert.Contains(LandedCostPlanning.Validate(request), e => e.Contains("push_sequence must be greater than 1"));
    }

    [Fact]
    public void FirstPush_IsBasedOnTheGrpoLines_WithMergedCostLinesInSapSpelling()
    {
        var request = Request(("c02", 1000000), ("C02", 250000), ("C08", 180000), ("EXC", 0));

        var plan = LandedCostPlanning.Build(request, Grpo, GrpoLines, null, Array.Empty<SapBaseLine>(), Codes);

        Assert.Equal("SO-2026-0012", plan.IdempotencyKey);
        Assert.Equal(LandedCostPlanning.BaseTypeGrpo, plan.BaseType);
        Assert.Equal(345, plan.BaseEntry);
        Assert.Equal(2, plan.ItemLines.Count);
        Assert.Collection(plan.CostLines,
            l => { Assert.Equal("C02", l.LcCode); Assert.Equal(1250000, l.Amount); Assert.Equal("cash_value_before", l.AllocationBy); },
            l => { Assert.Equal("C08", l.LcCode); Assert.Equal(180000, l.Amount); });
        Assert.Equal(1430000, plan.Total);
    }

    [Fact]
    public void DeltaPush_IsBasedOnThePreviousLandedCostDocument()
    {
        var request = Request(("C08", 20000));
        request.PushSequence = 2;
        request.PreviousLcDocentry = 77;
        var previous = new SapLandedCostHeader(77, 1007, "SO-2026-0012");
        var previousLines = new List<SapBaseLine> { new(0, "LM-1001", 120, null), new(1, "LM-1002", 40, null) };

        var plan = LandedCostPlanning.Build(request, Grpo, GrpoLines, previous, previousLines, Codes);

        Assert.Equal("SO-2026-0012:2", plan.IdempotencyKey);
        Assert.Equal(LandedCostPlanning.BaseTypeLandedCost, plan.BaseType);
        Assert.Equal(77, plan.BaseEntry);
        Assert.Same(previousLines, plan.ItemLines);
    }

    [Fact]
    public void Guards_NoGrpo_CancelledGrpo_NoLines()
    {
        var request = Request(("C02", 1));

        var none = Assert.Throws<LandedCostGuardException>(() =>
            LandedCostPlanning.Build(request, null, GrpoLines, null, Array.Empty<SapBaseLine>(), Codes));
        Assert.Equal("GRPO_NOT_FOUND", none.Code);

        var cancelled = Assert.Throws<LandedCostGuardException>(() =>
            LandedCostPlanning.Build(request, Grpo with { Cancelled = true }, GrpoLines, null, Array.Empty<SapBaseLine>(), Codes));
        Assert.Equal("GRPO_CANCELLED", cancelled.Code);

        var empty = Assert.Throws<LandedCostGuardException>(() =>
            LandedCostPlanning.Build(request, Grpo, Array.Empty<SapBaseLine>(), null, Array.Empty<SapBaseLine>(), Codes));
        Assert.Equal("GRPO_NO_LINES", empty.Code);
    }

    [Fact]
    public void Guards_UnknownCode_AndAllZero()
    {
        var unknown = Assert.Throws<LandedCostGuardException>(() =>
            LandedCostPlanning.Build(Request(("C99", 5)), Grpo, GrpoLines, null, Array.Empty<SapBaseLine>(), Codes));
        Assert.Equal("LC_CODE_UNKNOWN", unknown.Code);
        Assert.Contains("C99", unknown.Message);

        var zero = Assert.Throws<LandedCostGuardException>(() =>
            LandedCostPlanning.Build(Request(("C02", 0), ("C08", 0.004)), Grpo, GrpoLines, null, Array.Empty<SapBaseLine>(), Codes));
        Assert.Equal("NO_COST_LINES", zero.Code);
    }

    [Fact]
    public void Guards_NegativeTotalNeedsApproval_AndNeverOnTheFirstPush()
    {
        var previous = new SapLandedCostHeader(77, 1007, "SO-2026-0012");
        var previousLines = new List<SapBaseLine> { new(0, "LM-1001", 120, null) };

        var delta = Request(("C02", -50000));
        delta.PushSequence = 2;
        delta.PreviousLcDocentry = 77;

        var refused = Assert.Throws<LandedCostGuardException>(() =>
            LandedCostPlanning.Build(delta, Grpo, GrpoLines, previous, previousLines, Codes));
        Assert.Equal("NEGATIVE_NOT_APPROVED", refused.Code);

        delta.ApproveNegative = true;
        var plan = LandedCostPlanning.Build(delta, Grpo, GrpoLines, previous, previousLines, Codes);
        Assert.Equal(-50000, plan.Total);

        var first = Request(("C02", -1));
        first.ApproveNegative = true;
        var firstRefused = Assert.Throws<LandedCostGuardException>(() =>
            LandedCostPlanning.Build(first, Grpo, GrpoLines, null, Array.Empty<SapBaseLine>(), Codes));
        Assert.Equal("NEGATIVE_FIRST_PUSH", firstRefused.Code);
    }

    [Fact]
    public void Guards_PreviousDocumentMissingOrOfAnotherContainer()
    {
        var delta = Request(("C02", 10));
        delta.PushSequence = 2;
        delta.PreviousLcDocentry = 77;
        var lines = new List<SapBaseLine> { new(0, "LM-1001", 120, null) };

        var missing = Assert.Throws<LandedCostGuardException>(() =>
            LandedCostPlanning.Build(delta, Grpo, GrpoLines, null, lines, Codes));
        Assert.Equal("PREVIOUS_LC_NOT_FOUND", missing.Code);

        var other = Assert.Throws<LandedCostGuardException>(() =>
            LandedCostPlanning.Build(delta, Grpo, GrpoLines, new SapLandedCostHeader(77, 1007, "SO-2026-0099"), lines, Codes));
        Assert.Equal("PREVIOUS_LC_MISMATCH", other.Code);

        var noLines = Assert.Throws<LandedCostGuardException>(() =>
            LandedCostPlanning.Build(delta, Grpo, GrpoLines, new SapLandedCostHeader(77, 1007, "SO-2026-0012"), Array.Empty<SapBaseLine>(), Codes));
        Assert.Equal("PREVIOUS_LC_NO_LINES", noLines.Code);
    }

    [Fact]
    public void AllocationBy_IsNormalisedPerCode()
    {
        var request = Request(("C02", 100));
        request.CostLines[0].AllocationBy = " Quantity ";

        var plan = LandedCostPlanning.Build(request, Grpo, GrpoLines, null, Array.Empty<SapBaseLine>(), Codes);

        Assert.Equal("quantity", plan.CostLines[0].AllocationBy);
    }
}
