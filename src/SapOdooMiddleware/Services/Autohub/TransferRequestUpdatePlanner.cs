using SapOdooMiddleware.Models.Inventory;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>Outcome of planning a transfer-request update against the current SAP snapshot.</summary>
public class TransferRequestUpdatePlan
{
    public List<string> Errors { get; } = new();
    /// <summary>Absolute quantity writes that actually change the document.</summary>
    public List<TransferRequestLineQuantityUpdate> QuantityWrites { get; } = new();
    public List<TransferRequestLineCreate> NewLines { get; } = new();
    /// <summary>Trimmed replacement comments; null when the request leaves them alone.</summary>
    public string? Comments { get; set; }
    /// <summary>True when everything requested already matches SAP — nothing to write.</summary>
    public bool AlreadyApplied { get; set; }
}

/// <summary>
/// Pure validation/planning for the transfer-request update and close endpoints.
/// Everything here is COM-free so it can be unit tested; the DI API call only
/// receives a validated plan. SAP remains the final authority — it re-rejects
/// anything that changed between the snapshot and the write.
/// </summary>
public static class TransferRequestUpdatePlanner
{
    /// <summary>SAP OWTQ.Comments is nvarchar(254).</summary>
    public const int MaxCommentsLength = 254;
    private const double QtyTolerance = 0.000001;

    public static TransferRequestUpdatePlan PlanUpdate(
        TransferRequestSnapshot snapshot, TransferRequestUpdate request)
    {
        var plan = new TransferRequestUpdatePlan();
        if (!ValidateDocumentOpen(snapshot, plan)) return plan;

        if (request.Lines.Count == 0 && request.AddLines.Count == 0 && request.Comments is null)
        {
            plan.Errors.Add("Nothing to update: provide lines, add_lines, or comments.");
            return plan;
        }

        var byLineNum = snapshot.Lines.ToDictionary(l => l.LineNum);
        var seen = new HashSet<int>();
        foreach (var update in request.Lines)
        {
            if (!seen.Add(update.LineNum))
            {
                plan.Errors.Add($"Line {update.LineNum} is listed twice.");
                continue;
            }
            if (!byLineNum.TryGetValue(update.LineNum, out var line))
            {
                plan.Errors.Add($"Request {snapshot.DocEntry} has no line {update.LineNum}.");
                continue;
            }
            if (!string.Equals(line.LineStatus, "O", StringComparison.OrdinalIgnoreCase))
            {
                plan.Errors.Add(
                    $"Line {update.LineNum} ({line.ItemCode}) is closed and can no longer be changed.");
                continue;
            }
            if (update.Quantity <= 0)
            {
                plan.Errors.Add(
                    $"Line {update.LineNum} ({line.ItemCode}): quantity must be greater than zero.");
                continue;
            }
            double fulfilled = line.Quantity - line.OpenQty;
            if (update.Quantity + QtyTolerance < fulfilled)
            {
                plan.Errors.Add(
                    $"Line {update.LineNum} ({line.ItemCode}): {fulfilled} has already been " +
                    $"transferred — the quantity cannot go below that.");
                continue;
            }
            if (Math.Abs(update.Quantity - line.Quantity) <= QtyTolerance)
                continue; // already the stored quantity — nothing to write
            plan.QuantityWrites.Add(update);
        }

        for (int i = 0; i < request.AddLines.Count; i++)
        {
            var add = request.AddLines[i];
            if (string.IsNullOrWhiteSpace(add.ItemCode))
            {
                plan.Errors.Add($"add_lines[{i}]: item_code is required.");
                continue;
            }
            if (add.Quantity <= 0)
            {
                plan.Errors.Add($"add_lines[{i}] ({add.ItemCode}): quantity must be greater than zero.");
                continue;
            }
            add.ItemCode = add.ItemCode.Trim();
            plan.NewLines.Add(add);
        }

        if (request.Comments is not null)
        {
            var trimmed = request.Comments.Trim();
            if (trimmed.Length > MaxCommentsLength)
            {
                plan.Errors.Add($"comments must be at most {MaxCommentsLength} characters.");
            }
            else if (!string.Equals(trimmed, (snapshot.Comments ?? "").Trim(), StringComparison.Ordinal))
            {
                plan.Comments = trimmed;
            }
        }

        if (plan.Errors.Count > 0) return plan;
        if (plan.QuantityWrites.Count == 0 && plan.NewLines.Count == 0 && plan.Comments is null)
            plan.AlreadyApplied = true;
        return plan;
    }

    /// <summary>Errors preventing a close; empty when the document can be closed.
    /// A document that is already closed or cancelled is reported via
    /// <paramref name="alreadyClosed"/> so the endpoint can answer idempotently.</summary>
    public static List<string> ValidateClose(TransferRequestSnapshot snapshot, out bool alreadyClosed)
    {
        alreadyClosed =
            string.Equals(snapshot.Canceled, "Y", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.DocStatus, "O", StringComparison.OrdinalIgnoreCase);
        return new List<string>();
    }

    private static bool ValidateDocumentOpen(TransferRequestSnapshot snapshot, TransferRequestUpdatePlan plan)
    {
        if (string.Equals(snapshot.Canceled, "Y", StringComparison.OrdinalIgnoreCase))
        {
            plan.Errors.Add($"Transfer request {snapshot.DocEntry} is cancelled.");
            return false;
        }
        if (!string.Equals(snapshot.DocStatus, "O", StringComparison.OrdinalIgnoreCase))
        {
            plan.Errors.Add($"Transfer request {snapshot.DocEntry} is closed.");
            return false;
        }
        return true;
    }
}
