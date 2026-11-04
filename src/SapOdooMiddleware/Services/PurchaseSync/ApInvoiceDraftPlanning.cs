using SapOdooMiddleware.Models.PurchaseSync;

namespace SapOdooMiddleware.Services.PurchaseSync;

/// <summary>
/// Plans the SAP A/P invoice draft of a container from the supplier invoice Odoo holds and the container's GRPO lines.
/// Pure and SAP-free so it is unit-tested.
///
/// Rules:
/// <list type="bullet">
/// <item>Every GRPO line must be matched to an invoice line with the same item code and quantity. A free invoice line
/// (100% discount or zero amount) pairs first with a free GRPO line (100% discount or zero price), a charged one with a
/// charged one; a pair across that split is allowed when the quantity is the same, and noted (a GRPO line received at
/// zero for a charged item is repriced from the invoice).</item>
/// <item>Each draft line is priced as on the invoice: unit price and discount.</item>
/// <item>Invoice lines whose item is on no GRPO line are not stock: when they carry an amount they become the
/// display/marketing additional expense; free ones are only noted.</item>
/// <item>Item lines + display items + freight must equal the invoice total within the tolerance.</item>
/// </list>
/// </summary>
public static class ApInvoiceDraftPlanning
{
    private const double FreeDiscount = 99.999;

    /// <summary>Request-shape problems (HTTP 400). Empty when the request can be planned.</summary>
    public static List<string> Validate(ApInvoiceDraftRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.OdooShipmentId)) errors.Add("odoo_shipment_id is required.");
        if (string.IsNullOrWhiteSpace(request.CardCode)) errors.Add("card_code is required.");
        if (string.IsNullOrWhiteSpace(request.InvoiceNumber)) errors.Add("invoice_number is required (the supplier invoice number).");
        if (request.InvoiceDate == default) errors.Add("invoice_date is required.");
        if (string.IsNullOrWhiteSpace(request.Currency)) errors.Add("currency is required.");
        if (request.GrpoDocentry <= 0) errors.Add("grpo_docentry must be greater than zero.");
        if (request.InvoiceTotalFc <= 0) errors.Add("invoice_total_fc must be greater than zero.");
        if (request.FreightFc < 0) errors.Add("freight_fc cannot be negative.");
        if (request.FreightFc > 0 && request.FreightExpenseCode is not > 0)
            errors.Add("freight_expense_code is required when the invoice has freight (the OEXD code of \"Liqui Moly freight\").");
        if (request.Lines is null || request.Lines.Count == 0) errors.Add("lines must hold the invoice lines.");
        else if (request.Lines.Any(l => string.IsNullOrWhiteSpace(l.ItemCode) || l.Quantity <= 0))
            errors.Add("every line needs an item_code and a quantity above zero.");
        return errors;
    }

    public static string Normalize(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    private static bool IsFree(ApInvoiceDraftLineRequest l) =>
        l.IsFree || l.DiscountPercent >= FreeDiscount || Math.Abs(l.Amount) < 0.005;

    private static bool IsFree(ApGrpoLine g) => g.DiscountPercent >= FreeDiscount || Math.Abs(g.Price) < 0.000001;

    private static decimal LineTotal(double qty, double price, double discount) =>
        Math.Round((decimal)qty * (decimal)price * (1m - (decimal)discount / 100m), 2, MidpointRounding.AwayFromZero);

    private static bool SameQty(double a, double b) => Math.Abs(a - b) < 0.0001;

    /// <summary>Builds the plan, or raises <see cref="ApInvoiceGuardException"/> naming every problem found.</summary>
    public static ApDraftPlan Build(ApInvoiceDraftRequest request, IReadOnlyList<ApGrpoLine> grpoLines)
    {
        if (grpoLines.Count == 0)
            throw new ApInvoiceGuardException("GRPO_EMPTY", $"GRPO {request.GrpoDocentry} has no lines.");

        var problems = new List<string>();
        var notes = new List<string>();
        foreach (var g in grpoLines)
        {
            if (g.LineStatus == "C" || g.OpenQuantity + 0.0001 < g.Quantity)
                problems.Add($"GRPO line {g.LineNum} ({g.ItemCode}) is already invoiced (open {g.OpenQuantity:0.##} of {g.Quantity:0.##}).");
        }
        if (problems.Count > 0)
            throw new ApInvoiceGuardException("GRPO_ALREADY_INVOICED", string.Join(" ", problems));

        var grpoItems = new HashSet<string>(grpoLines.Select(g => Normalize(g.ItemCode)));
        var unused = request.Lines.Select((l, i) => (Line: l, Index: i))
            .Where(x => grpoItems.Contains(Normalize(x.Line.ItemCode))).ToList();
        var pairs = new Dictionary<int, ApInvoiceDraftLineRequest>();   // GRPO LineNum → invoice line

        // pass 1: same item, same free/charged class, same quantity; pass 2: same item and quantity, either class
        for (int pass = 1; pass <= 2; pass++)
        {
            foreach (var g in grpoLines.OrderBy(g => g.LineNum))
            {
                if (pairs.ContainsKey(g.LineNum)) continue;
                var match = unused.FirstOrDefault(x =>
                    Normalize(x.Line.ItemCode) == Normalize(g.ItemCode) && SameQty(x.Line.Quantity, g.Quantity)
                    && (pass == 2 || IsFree(x.Line) == IsFree(g)));
                if (match.Line is null) continue;
                pairs[g.LineNum] = match.Line;
                unused.Remove(match);
                if (pass == 2)
                    notes.Add(IsFree(match.Line)
                        ? $"{g.ItemCode} (GRPO line {g.LineNum}): received with a price but free on the invoice; invoiced at 0."
                        : $"{g.ItemCode} (GRPO line {g.LineNum}): received at zero but charged on the invoice; invoiced at {match.Line.UnitPrice:0.####}.");
            }
        }

        foreach (var g in grpoLines.Where(g => !pairs.ContainsKey(g.LineNum)))
        {
            var sameItem = request.Lines.Where(l => Normalize(l.ItemCode) == Normalize(g.ItemCode)).ToList();
            problems.Add(sameItem.Count == 0
                ? $"{g.ItemCode} (GRPO line {g.LineNum}, qty {g.Quantity:0.##}) is not on the invoice."
                : $"{g.ItemCode} (GRPO line {g.LineNum}): received {g.Quantity:0.##}, invoice has {string.Join(" + ", sameItem.Select(l => l.Quantity.ToString("0.##")))}.");
        }
        foreach (var (line, _) in unused)
            problems.Add($"{line.ItemCode}: invoice line of {line.Quantity:0.##} has no matching GRPO line.");
        if (problems.Count > 0)
            throw new ApInvoiceGuardException("LINES_DO_NOT_MATCH", string.Join(" ", problems));

        var items = grpoLines.OrderBy(g => g.LineNum).Select(g =>
        {
            var inv = pairs[g.LineNum];
            // A free line keeps its list price with a 100% discount, as on the invoice (and as the SAP rule 42101
            // requires of a zero-value line).
            bool free = IsFree(inv);
            double price = inv.UnitPrice;
            double discount = free ? 100 : inv.DiscountPercent;
            return new ApDraftItemLine(g.LineNum, g.ItemCode, g.Quantity, price, discount, LineTotal(g.Quantity, price, discount));
        }).ToList();

        var nonStock = request.Lines.Where(l => !grpoItems.Contains(Normalize(l.ItemCode))).ToList();
        var marketing = nonStock.Where(l => !IsFree(l)).ToList();
        decimal marketingTotal = marketing.Sum(l => Math.Round((decimal)l.Amount, 2, MidpointRounding.AwayFromZero));
        int freeNonStock = nonStock.Count - marketing.Count;
        if (freeNonStock > 0)
            notes.Add($"{freeNonStock} free invoice line(s) with no stock item (promotional material) are not in SAP.");
        if (marketingTotal > 0 && request.MarketingExpenseCode is not > 0)
            throw new ApInvoiceGuardException("MARKETING_CODE_MISSING",
                $"The invoice has {marketingTotal:0.00} {request.Currency} of display/marketing items " +
                $"({string.Join(", ", marketing.Select(l => l.ItemCode))}) but no marketing_expense_code is set.");

        decimal itemTotal = items.Sum(i => i.LineTotal);
        decimal freight = Math.Round((decimal)request.FreightFc, 2, MidpointRounding.AwayFromZero);
        decimal expected = itemTotal + marketingTotal + freight;
        decimal invoiceTotal = Math.Round((decimal)request.InvoiceTotalFc, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(expected - invoiceTotal) > (decimal)request.Tolerance)
            throw new ApInvoiceGuardException("TOTAL_MISMATCH",
                $"Item lines {itemTotal:0.00} + display/marketing {marketingTotal:0.00} + freight {freight:0.00} = {expected:0.00} " +
                $"{request.Currency}, but the invoice total is {invoiceTotal:0.00}. Check the invoice lines in Odoo.");

        return new ApDraftPlan(items, itemTotal, marketingTotal,
            marketing.Select(l => $"{l.ItemCode} {l.Name}".Trim()).ToList(), freight, expected, notes);
    }
}
