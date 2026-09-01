using SapOdooMiddleware.Models.Inventory;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>Outcome of planning a pick-list write against the current SAP snapshot.</summary>
public class PickListPlan
{
    public List<string> Errors { get; } = new();
    public List<PickListLineWrite> Lines { get; } = new();
    /// <summary>True when every requested line already matches SAP — nothing to write.</summary>
    public bool AlreadyApplied { get; set; }
    /// <summary>Auto-generated audit note for bin changes; null when no bin changed.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// Pure validation/planning for the pick-list write endpoints. Everything here is
/// COM-free so it can be unit tested; the DI API call only receives a validated plan.
/// </summary>
public static class PickListUpdatePlanner
{
    /// <summary>SAP OPKL.Remarks is nvarchar(254); older note lines are trimmed from the front.</summary>
    public const int MaxRemarksLength = 254;
    private const double QtyTolerance = 0.000001;

    public static PickListPlan PlanAllocationUpdate(
        PickListSnapshot snapshot,
        PickListAllocationUpdate request,
        IReadOnlyDictionary<int, IReadOnlyList<BinOption>> binStockByPickEntry,
        DateTime utcNow)
    {
        var plan = new PickListPlan();
        if (!ValidateDocumentOpen(snapshot, plan)) return plan;
        if (request.Lines.Count == 0)
        {
            plan.Errors.Add("At least one line is required.");
            return plan;
        }

        var noteParts = new List<string>();
        bool anyChange = false;
        foreach (var lineRequest in request.Lines)
        {
            var line = FindLine(snapshot, lineRequest.PickEntry, plan);
            if (line is null) continue;
            if (line.PickStatus != "R")
            {
                plan.Errors.Add(
                    $"Line {line.PickEntry} ({line.ItemCode}) is not in Released status — " +
                    "only released lines can be re-binned.");
                continue;
            }
            var allocations = NormalizeAllocations(lineRequest.Allocations, line, plan);
            if (allocations is null) continue;

            double total = allocations.Sum(a => a.Quantity);
            if (total - line.ReleasedQty > QtyTolerance)
            {
                plan.Errors.Add(
                    $"Line {line.PickEntry} ({line.ItemCode}): allocation total {total} exceeds " +
                    $"the released quantity {line.ReleasedQty}.");
                continue;
            }
            ValidateBinStock(line, allocations, binStockByPickEntry, plan);

            if (!AllocationsMatch(line.Allocations, allocations))
            {
                anyChange = true;
                noteParts.Add(DescribeBinChange(line, allocations, binStockByPickEntry));
            }
            plan.Lines.Add(new PickListLineWrite
            {
                PickEntry = line.PickEntry,
                PickedQty = null,
                Allocations = allocations,
            });
        }

        if (plan.Errors.Count > 0) return plan;
        if (!anyChange)
        {
            plan.AlreadyApplied = true;
            plan.Lines.Clear();
            return plan;
        }
        plan.Note = BuildNote(request.ChangedBy, utcNow, noteParts);
        return plan;
    }

    public static PickListPlan PlanPick(
        PickListSnapshot snapshot,
        PickListPickRequest request,
        IReadOnlyDictionary<int, IReadOnlyList<BinOption>> binStockByPickEntry,
        IReadOnlyDictionary<string, bool> binManagedByWhs,
        DateTime utcNow)
    {
        var plan = new PickListPlan();
        if (!ValidateDocumentOpen(snapshot, plan)) return plan;
        if (request.Lines.Count == 0)
        {
            plan.Errors.Add("At least one line is required.");
            return plan;
        }

        var noteParts = new List<string>();
        bool anyChange = false;
        foreach (var lineRequest in request.Lines)
        {
            var line = FindLine(snapshot, lineRequest.PickEntry, plan);
            if (line is null) continue;

            double target = lineRequest.PickedQty;
            double maxPickable = line.PickedQty + line.ReleasedQty;
            if (target < line.PickedQty - QtyTolerance)
            {
                plan.Errors.Add(
                    $"Line {line.PickEntry} ({line.ItemCode}): picked quantity {target} is below the " +
                    $"already-picked {line.PickedQty} — un-picking is not supported from the app.");
                continue;
            }
            if (target - maxPickable > QtyTolerance)
            {
                plan.Errors.Add(
                    $"Line {line.PickEntry} ({line.ItemCode}): picked quantity {target} exceeds the " +
                    $"total releasable quantity {maxPickable}.");
                continue;
            }

            bool binManaged = binManagedByWhs.TryGetValue(line.WhsCode, out var managed) && managed;
            var allocations = NormalizeAllocations(lineRequest.Allocations, line, plan, allowEmpty: !binManaged);
            if (allocations is null) continue;
            if (binManaged)
            {
                double total = allocations.Sum(a => a.Quantity);
                if (Math.Abs(total - target) > QtyTolerance)
                {
                    plan.Errors.Add(
                        $"Line {line.PickEntry} ({line.ItemCode}): bin allocations total {total} must " +
                        $"equal the picked quantity {target}.");
                    continue;
                }
                ValidateBinStock(line, allocations, binStockByPickEntry, plan);
            }
            else if (allocations.Count > 0)
            {
                plan.Errors.Add(
                    $"Line {line.PickEntry} ({line.ItemCode}): warehouse {line.WhsCode} is not " +
                    "bin-managed; omit allocations.");
                continue;
            }

            bool qtyChanged = Math.Abs(target - line.PickedQty) > QtyTolerance;
            bool binsChanged = binManaged && !AllocationsMatch(line.Allocations, allocations);
            if (qtyChanged || binsChanged) anyChange = true;
            if (binsChanged) noteParts.Add(DescribeBinChange(line, allocations, binStockByPickEntry));

            plan.Lines.Add(new PickListLineWrite
            {
                PickEntry = line.PickEntry,
                PickedQty = target,
                Allocations = allocations,
            });
        }

        if (plan.Errors.Count > 0) return plan;
        if (!anyChange)
        {
            plan.AlreadyApplied = true;
            plan.Lines.Clear();
            return plan;
        }
        if (noteParts.Count > 0) plan.Note = BuildNote(request.ChangedBy, utcNow, noteParts);
        return plan;
    }

    /// <summary>Appends the note to the existing remarks, trimming oldest content to fit OPKL.Remarks.</summary>
    public static string AppendNote(string existingRemarks, string note)
    {
        var combined = string.IsNullOrWhiteSpace(existingRemarks)
            ? note
            : existingRemarks.TrimEnd() + "\n" + note;
        if (combined.Length <= MaxRemarksLength) return combined;
        // Keep the newest content: trim from the front at a line boundary when possible.
        var overflow = combined.Length - MaxRemarksLength;
        var trimmed = combined[overflow..];
        var firstBreak = trimmed.IndexOf('\n');
        if (firstBreak >= 0 && firstBreak < trimmed.Length - 1) trimmed = trimmed[(firstBreak + 1)..];
        return trimmed.Length <= MaxRemarksLength ? trimmed : trimmed[^MaxRemarksLength..];
    }

    private static bool ValidateDocumentOpen(PickListSnapshot snapshot, PickListPlan plan)
    {
        if (snapshot.Canceled)
        {
            plan.Errors.Add($"Pick list {snapshot.AbsEntry} is cancelled.");
            return false;
        }
        if (snapshot.Status is not ("R" or "P"))
        {
            plan.Errors.Add(
                $"Pick list {snapshot.AbsEntry} has status '{snapshot.Status}' — only Released or " +
                "Partially Picked pick lists can be worked.");
            return false;
        }
        return true;
    }

    private static PickListLineSnapshot? FindLine(
        PickListSnapshot snapshot, int pickEntry, PickListPlan plan)
    {
        var line = snapshot.Lines.FirstOrDefault(candidate => candidate.PickEntry == pickEntry);
        if (line is null)
            plan.Errors.Add($"Pick list {snapshot.AbsEntry} has no line with pick_entry {pickEntry}.");
        return line;
    }

    private static List<PickBinAllocation>? NormalizeAllocations(
        List<PickBinAllocation> requested,
        PickListLineSnapshot line,
        PickListPlan plan,
        bool allowEmpty = false)
    {
        if (requested.Count == 0)
        {
            if (allowEmpty) return new List<PickBinAllocation>();
            plan.Errors.Add(
                $"Line {line.PickEntry} ({line.ItemCode}): at least one bin allocation is required.");
            return null;
        }
        var seen = new HashSet<int>();
        var normalized = new List<PickBinAllocation>();
        foreach (var allocation in requested)
        {
            if (allocation.BinAbs <= 0)
            {
                plan.Errors.Add($"Line {line.PickEntry}: bin_abs must be a positive integer.");
                return null;
            }
            if (allocation.Quantity <= 0)
            {
                plan.Errors.Add(
                    $"Line {line.PickEntry}: allocation quantity for bin {allocation.BinAbs} must be " +
                    "greater than zero.");
                return null;
            }
            if (!seen.Add(allocation.BinAbs))
            {
                plan.Errors.Add($"Line {line.PickEntry}: bin {allocation.BinAbs} is listed twice.");
                return null;
            }
            normalized.Add(new PickBinAllocation { BinAbs = allocation.BinAbs, Quantity = allocation.Quantity });
        }
        return normalized.OrderBy(a => a.BinAbs).ToList();
    }

    private static void ValidateBinStock(
        PickListLineSnapshot line,
        List<PickBinAllocation> allocations,
        IReadOnlyDictionary<int, IReadOnlyList<BinOption>> binStockByPickEntry,
        PickListPlan plan)
    {
        binStockByPickEntry.TryGetValue(line.PickEntry, out var stock);
        foreach (var allocation in allocations)
        {
            var bin = stock?.FirstOrDefault(candidate => candidate.BinAbs == allocation.BinAbs);
            if (bin is null)
            {
                plan.Errors.Add(
                    $"Line {line.PickEntry} ({line.ItemCode}): bin {allocation.BinAbs} holds no stock " +
                    $"of this item in warehouse {line.WhsCode}.");
                continue;
            }
            if (allocation.Quantity - (double)bin.OnHandQty > QtyTolerance)
            {
                plan.Errors.Add(
                    $"Line {line.PickEntry} ({line.ItemCode}): bin {bin.BinCode} holds {bin.OnHandQty} " +
                    $"but {allocation.Quantity} was requested.");
            }
        }
    }

    private static bool AllocationsMatch(
        List<PickListBinSnapshot> current, List<PickBinAllocation> requested)
    {
        // Compare against the line's total per bin (released + picked): the request
        // carries the full breakdown, the snapshot splits it across two columns.
        var currentByBin = current
            .GroupBy(row => row.BinAbs)
            .ToDictionary(g => g.Key, g => g.Sum(row => row.ReleasedQty + row.PickedQty));
        var requestedByBin = requested.ToDictionary(a => a.BinAbs, a => a.Quantity);
        if (currentByBin.Count != requestedByBin.Count) return false;
        foreach (var (binAbs, quantity) in requestedByBin)
        {
            if (!currentByBin.TryGetValue(binAbs, out var existing)) return false;
            if (Math.Abs(existing - quantity) > QtyTolerance) return false;
        }
        return true;
    }

    private static string DescribeBinChange(
        PickListLineSnapshot line,
        List<PickBinAllocation> allocations,
        IReadOnlyDictionary<int, IReadOnlyList<BinOption>> binStockByPickEntry)
    {
        binStockByPickEntry.TryGetValue(line.PickEntry, out var stock);
        string BinLabel(int binAbs) =>
            line.Allocations.FirstOrDefault(row => row.BinAbs == binAbs)?.BinCode
            ?? stock?.FirstOrDefault(candidate => candidate.BinAbs == binAbs)?.BinCode
            ?? binAbs.ToString();
        var before = line.Allocations.Count == 0
            ? "none"
            : string.Join("+", line.Allocations
                .GroupBy(row => row.BinAbs)
                .Select(g => $"{BinLabel(g.Key)}({g.Sum(row => row.ReleasedQty + row.PickedQty)})"));
        var after = string.Join("+", allocations.Select(a => $"{BinLabel(a.BinAbs)}({a.Quantity})"));
        return $"L{line.PickEntry} {line.ItemCode}: {before} -> {after}";
    }

    private static string BuildNote(string changedBy, DateTime utcNow, List<string> parts)
    {
        var actor = string.IsNullOrWhiteSpace(changedBy) ? "app" : changedBy.Trim();
        return $"[App {utcNow:yyyy-MM-dd HH:mm}Z {actor}] {string.Join("; ", parts)}";
    }
}
