namespace SapOdooMiddleware.Services;

/// <summary>
/// Pure allocation logic for SAP B1 pick lists.  Takes a snapshot of
/// bin stock (keyed by BinCode), the configured priority list, the
/// fallback flag and a set of lines to allocate; produces a per-line
/// plan that states which warehouse the SO line should post to and
/// which bins to release from on the pick list.
///
/// No SAP / COM dependencies — compiles and unit-tests on any platform.
///
/// Zero-stock policy ("Choice X"): a line is kept whole in a single
/// warehouse.  That warehouse is chosen as the warehouse of the
/// highest-priority bin that holds any stock for the line's item; if
/// fallback is enabled and no priority bin has stock, the largest
/// non-priority bin's warehouse is used.  If no bin anywhere has stock
/// the line falls back to DefaultWarehouseCode so the SO still posts,
/// and the plan's Shortfall equals the full required quantity so the
/// pick list builder skips that line and a warning is surfaced.
/// </summary>
public static class BinAllocationPlanner
{
    /// <summary>On-hand stock for a single bin across multiple items.</summary>
    public sealed record BinStock(
        int AbsEntry,
        string BinCode,
        string WhsCode,
        Dictionary<string, double> OnHandByItemCode);

    /// <summary>One bin's contribution to a single line's required qty.</summary>
    public sealed record BinPick(
        int AbsEntry,
        string BinCode,
        string WhsCode,
        double Qty,
        bool IsFallback);

    /// <summary>Caller-supplied description of a line needing allocation.</summary>
    public sealed record LineInput(
        int LineIdx,
        string ItemCode,
        double Required,
        string? ExplicitWhsCode);

    /// <summary>Result for one line: chosen warehouse, the bins to pull
    /// from, and any shortfall that could not be covered.</summary>
    public sealed record LinePlan(
        int LineIdx,
        string ItemCode,
        double Required,
        string WhsCode,
        IReadOnlyList<BinPick> Picks,
        double Shortfall)
    {
        public bool FullyAllocated => Shortfall <= 0 && Picks.Count > 0;
    }

    /// <summary>
    /// Compute the allocation plan for every input line.  <paramref name="binInfo"/>
    /// is cloned internally so the caller's dictionary is never mutated — the
    /// planner decrements its working copy as lines claim stock so multiple
    /// lines for the same item do not over-allocate the same bin.
    /// </summary>
    public static IReadOnlyList<LinePlan> Plan(
        IReadOnlyList<LineInput> lines,
        IReadOnlyList<string> binPriority,
        IReadOnlyDictionary<string, BinStock> binInfo,
        bool allowFallback,
        string defaultWarehouseCode)
    {
        // Clone stock so per-line decrements don't mutate the caller's data.
        var mutableStock = binInfo.ToDictionary(
            kv => kv.Key,
            kv => new BinStock(
                kv.Value.AbsEntry,
                kv.Value.BinCode,
                kv.Value.WhsCode,
                new Dictionary<string, double>(kv.Value.OnHandByItemCode)),
            StringComparer.OrdinalIgnoreCase);

        var prioritySet = new HashSet<string>(
            binPriority ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var results = new List<LinePlan>(lines.Count);

        foreach (var line in lines)
        {
            // Phase 0: honour an explicit WhsCode on the line when set.
            // Odoo rarely sends this today, but the planner preserves
            // the caller's choice when it does.  During pick-list refresh
            // the caller passes each SAP line's CURRENT WhsCode so the
            // existing SAP line is never repointed (per product decision:
            // "new lines only" for updates).
            string? whsLocked = !string.IsNullOrWhiteSpace(line.ExplicitWhsCode)
                ? line.ExplicitWhsCode
                : null;

            // Phase 1: walk priority bins in order and lock onto the
            // warehouse of the first one that holds any stock for this
            // item.  Note: we don't decrement here — this is just a
            // "where should this line live?" scan.
            if (whsLocked is null && binPriority is not null)
            {
                foreach (var binCode in binPriority)
                {
                    if (!mutableStock.TryGetValue(binCode, out var bin)) continue;
                    if (!bin.OnHandByItemCode.TryGetValue(line.ItemCode, out var qty)
                        || qty <= 0) continue;
                    whsLocked = bin.WhsCode;
                    break;
                }
            }

            // Phase 1b (fallback): no priority bin has stock. If fallback
            // is enabled, lock onto whichever non-priority bin has the
            // largest stock for this item; otherwise leave unlocked.
            if (whsLocked is null && allowFallback)
            {
                var best = mutableStock
                    .Where(kv => !prioritySet.Contains(kv.Key))
                    .Where(kv => kv.Value.OnHandByItemCode.TryGetValue(line.ItemCode, out var q) && q > 0)
                    .OrderByDescending(kv => kv.Value.OnHandByItemCode[line.ItemCode])
                    .Select(kv => (string?)kv.Value.WhsCode)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(best))
                    whsLocked = best;
            }

            // Nothing has stock anywhere. Keep the SO postable by
            // falling back to the configured default warehouse; the
            // full required qty becomes Shortfall.
            if (whsLocked is null)
            {
                results.Add(new LinePlan(
                    line.LineIdx, line.ItemCode, line.Required,
                    defaultWarehouseCode ?? string.Empty,
                    Array.Empty<BinPick>(),
                    line.Required));
                continue;
            }

            // Phase 2: allocate from priority bins in the locked warehouse.
            double remaining = line.Required;
            var picks = new List<BinPick>();

            if (binPriority is not null)
            {
                foreach (var binCode in binPriority)
                {
                    if (remaining <= 0) break;
                    if (!mutableStock.TryGetValue(binCode, out var bin)) continue;
                    if (!string.Equals(bin.WhsCode, whsLocked, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!bin.OnHandByItemCode.TryGetValue(line.ItemCode, out var available)
                        || available <= 0) continue;

                    double take = Math.Min(remaining, available);
                    picks.Add(new BinPick(bin.AbsEntry, binCode, bin.WhsCode, take, false));
                    remaining -= take;
                    bin.OnHandByItemCode[line.ItemCode] = available - take;
                }
            }

            // Phase 3 (fallback): non-priority bins in the locked warehouse,
            // largest-first.  Only runs when AllowFallbackBinAllocation=true
            // AND priority bins couldn't cover the full qty.
            if (remaining > 0 && allowFallback)
            {
                var fallbackBins = mutableStock
                    .Where(kv => !prioritySet.Contains(kv.Key))
                    .Where(kv => string.Equals(kv.Value.WhsCode, whsLocked, StringComparison.OrdinalIgnoreCase))
                    .Where(kv => kv.Value.OnHandByItemCode.TryGetValue(line.ItemCode, out var q) && q > 0)
                    .OrderByDescending(kv => kv.Value.OnHandByItemCode[line.ItemCode])
                    .ToList();

                foreach (var kv in fallbackBins)
                {
                    if (remaining <= 0) break;
                    var bin = kv.Value;
                    double available = bin.OnHandByItemCode[line.ItemCode];
                    double take = Math.Min(remaining, available);
                    picks.Add(new BinPick(bin.AbsEntry, kv.Key, bin.WhsCode, take, true));
                    remaining -= take;
                    bin.OnHandByItemCode[line.ItemCode] = available - take;
                }
            }

            results.Add(new LinePlan(
                line.LineIdx, line.ItemCode, line.Required,
                whsLocked, picks, Math.Max(remaining, 0)));
        }

        return results;
    }
}
