using SapOdooMiddleware.Models.PurchaseSync;

namespace SapOdooMiddleware.Services.PurchaseSync;

/// <summary>A goods receipt (OPDN) as the planner needs it.</summary>
public sealed record LandedCostGrpo(int DocEntry, int DocNum, string CardCode, bool Cancelled, string? ShipRef);

/// <summary>An item line of a GRPO (PDN1) or of a landed cost document (IPF1).</summary>
public sealed record SapBaseLine(int LineNum, string ItemCode, double Quantity, string? WhsCode);

/// <summary>A landed cost document (OIPF) found by its <c>U_OdooShipmentId</c> or DocEntry.</summary>
public sealed record SapLandedCostHeader(int DocEntry, int DocNum, string? OdooShipmentId);

/// <summary>Everything the DI API call needs, computed and checked up front.</summary>
public sealed record LandedCostPlan(
    string IdempotencyKey,
    int BaseType,
    int BaseEntry,
    IReadOnlyList<SapBaseLine> ItemLines,
    IReadOnlyList<LandedCostPlanCostLine> CostLines,
    double Total);

public sealed record LandedCostPlanCostLine(string LcCode, double Amount, string AllocationBy);

/// <summary>
/// The pure part of the landed cost push (spec v4 §6.3), kept out of the DI API code so it is unit-tested: the
/// idempotency key, the request validation, the §6.3 guards and the choice of base document (the GRPO for the first
/// push, the previous landed cost document for a delta).
/// </summary>
public static class LandedCostPlanning
{
    public const int BaseTypeGrpo = 20;
    public const int BaseTypeLandedCost = 69;

    public const string DefaultAllocation = "cash_value_before";

    public static readonly IReadOnlyList<string> AllocationValues = new[]
    {
        "cash_value_before", "cash_value_after", "quantity", "weight", "volume", "equally",
    };

    /// <summary><c>ref</c> for the first push, <c>ref:n</c> from the second on. Clipped to the UDF's 50 characters
    /// from the left so the sequence suffix always survives.</summary>
    public static string IdempotencyKey(string odooShipmentId, int pushSequence)
    {
        string reference = (odooShipmentId ?? string.Empty).Trim();
        string key = pushSequence <= 1 ? reference : $"{reference}:{pushSequence}";
        return key.Length <= 50 ? key : key[^50..];
    }

    /// <summary>Request-shape problems (HTTP 400). Empty when the request can be planned.</summary>
    public static List<string> Validate(LandedCostPushRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.OdooShipmentId))
            errors.Add("odoo_shipment_id is required (the container order reference).");
        if (request.GrpoDocentry <= 0)
            errors.Add("grpo_docentry must be greater than zero.");
        if (request.PreviousLcDocentry is <= 0)
            errors.Add("previous_lc_docentry must be greater than zero when given.");
        if (request.PushSequence > 1 && request.PreviousLcDocentry is null)
            errors.Add("previous_lc_docentry is required when push_sequence is greater than 1 (a delta push).");
        if (request.PushSequence <= 1 && request.PreviousLcDocentry is not null)
            errors.Add("push_sequence must be greater than 1 when previous_lc_docentry is given.");
        if (request.CostLines is null || request.CostLines.Count == 0)
            errors.Add("cost_lines must hold at least one line.");
        else
        {
            for (int i = 0; i < request.CostLines.Count; i++)
            {
                var line = request.CostLines[i];
                if (string.IsNullOrWhiteSpace(line.LcCode))
                    errors.Add($"cost_lines[{i}].lc_code is required.");
                if (double.IsNaN(line.AmountTzs) || double.IsInfinity(line.AmountTzs))
                    errors.Add($"cost_lines[{i}].amount_tzs is not a number.");
                if (!string.IsNullOrWhiteSpace(line.AllocationBy)
                    && !AllocationValues.Contains(line.AllocationBy.Trim().ToLowerInvariant()))
                    errors.Add($"cost_lines[{i}].allocation_by '{line.AllocationBy}' is not one of: {string.Join(", ", AllocationValues)}.");
            }
        }
        return errors;
    }

    /// <summary>
    /// Builds the plan or throws <see cref="LandedCostGuardException"/>. <paramref name="grpo"/> is null when the GRPO
    /// does not exist; <paramref name="previous"/> / <paramref name="previousLines"/> describe <c>previous_lc_docentry</c>
    /// when given; <paramref name="knownCodes"/> are the codes of OALC (matched case-insensitively, SAP's spelling is kept).
    /// </summary>
    public static LandedCostPlan Build(
        LandedCostPushRequest request,
        LandedCostGrpo? grpo,
        IReadOnlyList<SapBaseLine> grpoLines,
        SapLandedCostHeader? previous,
        IReadOnlyList<SapBaseLine> previousLines,
        IReadOnlyCollection<string> knownCodes)
    {
        string reference = request.OdooShipmentId.Trim();
        string key = IdempotencyKey(reference, request.PushSequence);

        if (grpo is null)
            throw new LandedCostGuardException("GRPO_NOT_FOUND", $"Goods receipt DocEntry={request.GrpoDocentry} does not exist in SAP (OPDN).");
        if (grpo.Cancelled)
            throw new LandedCostGuardException("GRPO_CANCELLED", $"Goods receipt {grpo.DocNum} (DocEntry={grpo.DocEntry}) is cancelled in SAP.");
        if (grpoLines.Count == 0)
            throw new LandedCostGuardException("GRPO_NO_LINES", $"Goods receipt {grpo.DocNum} (DocEntry={grpo.DocEntry}) has no item lines.");

        var costLines = MergeCostLines(request.CostLines, knownCodes);
        if (costLines.Count == 0)
            throw new LandedCostGuardException("NO_COST_LINES", "Every cost line is zero; nothing to push.");

        double total = Math.Round(costLines.Sum(l => l.Amount), 2, MidpointRounding.AwayFromZero);
        var negative = costLines.Where(l => l.Amount < 0).ToList();
        if (negative.Count > 0 && !request.ApproveNegative)
        {
            throw new LandedCostGuardException(
                "NEGATIVE_NOT_APPROVED",
                "Negative landed cost for code(s) " + string.Join(", ", negative.Select(l => $"{l.LcCode} ({l.Amount:0.00})")) +
                ". Tick \"negative landed cost approved\" on the container order and push again.");
        }
        if (negative.Count > 0 && request.PushSequence <= 1)
            throw new LandedCostGuardException("NEGATIVE_FIRST_PUSH", "The first push of a container cannot carry a negative cost line.");

        int baseType;
        int baseEntry;
        IReadOnlyList<SapBaseLine> itemLines;
        if (request.PreviousLcDocentry is { } prevEntry)
        {
            if (previous is null)
                throw new LandedCostGuardException("PREVIOUS_LC_NOT_FOUND", $"Landed cost document DocEntry={prevEntry} does not exist in SAP (OIPF).");
            if (!BelongsTo(previous.OdooShipmentId, reference))
            {
                throw new LandedCostGuardException(
                    "PREVIOUS_LC_MISMATCH",
                    $"Landed cost document {previous.DocNum} (DocEntry={prevEntry}) carries U_OdooShipmentId '{previous.OdooShipmentId}', not {reference}.");
            }
            if (previousLines.Count == 0)
                throw new LandedCostGuardException("PREVIOUS_LC_NO_LINES", $"Landed cost document {previous.DocNum} (DocEntry={prevEntry}) has no item lines.");
            baseType = BaseTypeLandedCost;
            baseEntry = prevEntry;
            itemLines = previousLines;
        }
        else
        {
            baseType = BaseTypeGrpo;
            baseEntry = grpo.DocEntry;
            itemLines = grpoLines;
        }

        return new LandedCostPlan(key, baseType, baseEntry, itemLines, costLines, total);
    }

    /// <summary>True when <paramref name="udfValue"/> is <paramref name="reference"/> or <c>reference:n</c>.</summary>
    public static bool BelongsTo(string? udfValue, string reference)
    {
        string value = (udfValue ?? string.Empty).Trim();
        if (value.Length == 0) return false;
        if (string.Equals(value, reference, StringComparison.OrdinalIgnoreCase)) return true;
        int colon = value.LastIndexOf(':');
        return colon > 0
            && string.Equals(value[..colon], reference, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value[(colon + 1)..], out _);
    }

    private static List<LandedCostPlanCostLine> MergeCostLines(IEnumerable<LandedCostLineRequest> lines, IReadOnlyCollection<string> knownCodes)
    {
        // SAP's own spelling of each code is what goes on the document; the match is case-insensitive.
        var sapCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var known in knownCodes)
            if (!string.IsNullOrWhiteSpace(known)) sapCodes.TryAdd(known.Trim(), known.Trim());

        var merged = new Dictionary<string, (double Amount, string Allocation)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var line in lines)
        {
            string code = line.LcCode.Trim().ToUpperInvariant();
            string allocation = string.IsNullOrWhiteSpace(line.AllocationBy)
                ? DefaultAllocation
                : line.AllocationBy.Trim().ToLowerInvariant();
            if (merged.TryGetValue(code, out var current))
                merged[code] = (current.Amount + line.AmountTzs, current.Allocation);
            else
            {
                merged[code] = (line.AmountTzs, allocation);
                order.Add(code);
            }
        }

        var result = new List<LandedCostPlanCostLine>();
        foreach (var code in order)
        {
            double amount = Math.Round(merged[code].Amount, 2, MidpointRounding.AwayFromZero);
            if (amount == 0) continue;
            if (!sapCodes.TryGetValue(code, out var sapCode))
            {
                throw new LandedCostGuardException(
                    "LC_CODE_UNKNOWN",
                    $"Landed cost code '{code}' does not exist in SAP (Administration ▸ Setup ▸ Purchasing ▸ Landed Costs). " +
                    "Create it there or fix x_sap_lc_code on the expense category.");
            }
            result.Add(new LandedCostPlanCostLine(sapCode, amount, merged[code].Allocation));
        }
        return result;
    }
}
