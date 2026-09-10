using SapOdooMiddleware.Models.Inventory;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Pure validation/planning for re-sourcing one released pick-list line to a
/// different warehouse. The write itself updates the underlying sales-order
/// line (RDR1.WhsCode); SAP propagates onto the existing pick list in place.
/// Only a fully unpicked, Released line may move — once any quantity is
/// picked, the picker's work in the old warehouse must not be invalidated.
/// </summary>
public static class PickLineWarehouseChangePlanner
{
    private const double PickedTolerance = 0.000001;

    public static PickLineWarehouseChangePlan Plan(
        PickListSnapshot snapshot,
        PickLineWarehouseChange request,
        IReadOnlyCollection<string> validWarehouseCodes)
    {
        var plan = new PickLineWarehouseChangePlan { PickEntry = request.PickEntry };

        if (snapshot.Canceled)
        {
            plan.Errors.Add($"Pick list {snapshot.AbsEntry} is cancelled.");
            return plan;
        }

        var target = (request.WhsCode ?? "").Trim();
        if (target.Length == 0)
        {
            plan.Errors.Add("whs_code is required.");
            return plan;
        }
        var known = validWarehouseCodes
            .FirstOrDefault(code => string.Equals(code?.Trim(), target, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            plan.Errors.Add($"Warehouse {target} was not found.");
            return plan;
        }
        plan.TargetWhs = known.Trim();

        var line = snapshot.Lines.FirstOrDefault(l => l.PickEntry == request.PickEntry);
        if (line is null)
        {
            plan.Errors.Add($"Pick list {snapshot.AbsEntry} has no line {request.PickEntry}.");
            return plan;
        }

        plan.OrderEntry = line.OrderEntry;
        plan.OrderLine = line.OrderLine;
        plan.ItemCode = line.ItemCode;
        plan.SourceWhs = line.WhsCode;

        var pickStatus = (line.PickStatus ?? "").Trim().ToUpperInvariant();
        if (line.PickedQty > PickedTolerance || pickStatus is "Y" or "P")
        {
            plan.Errors.Add(
                $"Line {request.PickEntry} ({line.ItemCode}) already has picked quantity — " +
                "undo the picking in SAP before changing its warehouse.");
            return plan;
        }
        if (pickStatus is "C" or "D")
        {
            plan.Errors.Add($"Line {request.PickEntry} ({line.ItemCode}) is closed.");
            return plan;
        }
        if (line.OrderEntry <= 0)
        {
            plan.Errors.Add(
                $"Line {request.PickEntry} ({line.ItemCode}) carries no sales-order reference; " +
                "its warehouse can only be changed in SAP.");
            return plan;
        }

        if (string.Equals(line.WhsCode?.Trim(), plan.TargetWhs, StringComparison.OrdinalIgnoreCase))
        {
            plan.AlreadyApplied = true;
        }
        return plan;
    }
}
