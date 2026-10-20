using System.Globalization;
using System.Text.Json.Nodes;
using SapOdooMiddleware.Models.PurchaseSync;

namespace SapOdooMiddleware.Services.PurchaseSync;

/// <summary>One Odoo bill line after mapping (pure data, testable).</summary>
public sealed record ResolvedBillLine(
    string Name,
    decimal Quantity,
    decimal PriceUnit,
    int AccountId,
    int? TaxId,
    int? AnalyticAccountId,
    string Source);

/// <summary>Everything the mapper needs besides the SAP document and the mapping tables.</summary>
public sealed record ApMoveContext(
    int PartnerId,
    int JournalId,
    int CurrencyId,
    string CompanyCurrency,
    bool InvoiceRateIsInverseOfDocRate,
    int? ReversedEntryId);

/// <summary>
/// Pure translation of an A/P invoice or credit memo into <c>account.move</c> values (spec v4 §6.1):
/// resolves every account and tax first (a miss throws a terminal <see cref="PurchaseSyncException"/>
/// and nothing is created), then builds the create payload. No Odoo calls here.
/// </summary>
public static class ApDocumentMapper
{
    public const string ErrAccountNotMapped = "ACCOUNT_NOT_MAPPED";
    public const string ErrTaxNotMapped = "TAX_NOT_MAPPED";
    public const string ErrTotalMismatch = "TOTAL_MISMATCH";

    /// <summary>
    /// The account.move fields that hold the SAP A/P DocEntry (spec v4 §5.2: the control centre's
    /// existing invoice / credit fields are reused; in_invoice → OPCH.DocEntry, in_refund → ORPC.DocEntry).
    /// </summary>
    public const string InvoiceDocEntryField = "x_sap_invoice_docentry";
    public const string CreditDocEntryField = "x_sap_credit_docentry";

    public static bool IsForeign(SapApDocument doc, string companyCurrency) =>
        !string.Equals(doc.DocCur?.Trim(), companyCurrency?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The SAP total the Odoo <c>amount_total</c> must match: FC total for foreign documents.</summary>
    public static decimal ExpectedTotal(SapApDocument doc, string companyCurrency) =>
        IsForeign(doc, companyCurrency) ? doc.DocTotalFc : doc.DocTotal;

    public static bool TotalsMatch(SapApDocument doc, string companyCurrency, decimal odooAmountTotal, decimal tolerance) =>
        Math.Abs(Math.Abs(odooAmountTotal) - Math.Abs(ExpectedTotal(doc, companyCurrency))) <= tolerance;

    public static List<ResolvedBillLine> ResolveLines(SapApDocument doc, IccMappings mappings, string companyCurrency)
    {
        bool foreign = IsForeign(doc, companyCurrency);
        var result = new List<ResolvedBillLine>();

        foreach (var line in doc.Lines)
        {
            int accountId;
            int? analyticId = null;
            string source;

            bool serviceLine = doc.IsServiceType || string.IsNullOrWhiteSpace(line.ItemCode);
            if (serviceLine)
            {
                var acct = line.AcctCode?.Trim() ?? string.Empty;
                if (acct.Length == 0 || !mappings.AccountByGlCode.TryGetValue(acct, out accountId))
                    throw new PurchaseSyncException(ErrAccountNotMapped,
                        $"line {line.LineNum}: SAP G/L account '{acct}' has no icc.sap.glaccount.map entry.");
                source = $"gl:{acct}";
            }
            else
            {
                if (!line.ItmsGrpCod.HasValue
                    || !mappings.AccountByItemGroup.TryGetValue(line.ItmsGrpCod.Value, out var mapped))
                    throw new PurchaseSyncException(ErrAccountNotMapped,
                        $"line {line.LineNum} item {line.ItemCode}: SAP item group '{line.ItmsGrpCod}' has no icc.sap.itemgroup.map entry.");
                accountId = mapped.AccountId;
                analyticId = mapped.AnalyticAccountId;
                source = $"itemgroup:{line.ItmsGrpCod}";
            }

            int? taxId = ResolveTax(line.VatGroup, mappings, $"line {line.LineNum}");

            decimal qty = line.Quantity == 0 ? 1m : Math.Abs(line.Quantity);
            decimal price = line.Price;
            if (price == 0m)
            {
                decimal total = foreign ? line.TotalFrgn : line.LineTotal;
                price = qty == 0 ? total : total / qty;
            }

            string name = string.IsNullOrWhiteSpace(line.ItemCode)
                ? (line.Dscription ?? $"Line {line.LineNum}")
                : $"{line.ItemCode} – {line.Dscription}".Trim(' ', '–');

            result.Add(new ResolvedBillLine(name, qty, Round6(price), accountId, taxId, analyticId, source));
        }

        foreach (var exp in doc.Expenses)
        {
            var acct = exp.ExpenseAccount?.Trim() ?? string.Empty;
            if (acct.Length == 0 || !mappings.AccountByGlCode.TryGetValue(acct, out var accountId))
                throw new PurchaseSyncException(ErrAccountNotMapped,
                    $"expense {exp.ExpnsCode} ({exp.ExpnsName}): SAP expense account '{acct}' has no icc.sap.glaccount.map entry.");

            int? taxId = ResolveTax(exp.VatGroup, mappings, $"expense {exp.ExpnsCode}");
            decimal amount = foreign ? exp.TotalFrgn : exp.LineTotal;

            result.Add(new ResolvedBillLine(
                exp.ExpnsName ?? $"Additional expense {exp.ExpnsCode}",
                1m, Round6(amount), accountId, taxId, null, $"expense:{exp.ExpnsCode}"));
        }

        return result;
    }

    private static int? ResolveTax(string? vatGroup, IccMappings mappings, string where)
    {
        var key = vatGroup?.Trim();
        if (string.IsNullOrEmpty(key))
            return null;
        if (!mappings.TaxByVatGroup.TryGetValue(key, out var taxId))
            throw new PurchaseSyncException(ErrTaxNotMapped,
                $"{where}: SAP VAT group '{key}' has no icc.sap.vatgroup.map entry.");
        return taxId;
    }

    /// <summary>The <c>account.move</c> create payload for the document.</summary>
    public static JsonObject BuildMoveValues(SapApDocument doc, ApMoveContext ctx, IReadOnlyList<ResolvedBillLine> lines)
    {
        bool foreign = IsForeign(doc, ctx.CompanyCurrency);

        var values = new JsonObject
        {
            ["move_type"] = doc.IsCreditMemo ? "in_refund" : "in_invoice",
            ["partner_id"] = ctx.PartnerId,
            ["journal_id"] = ctx.JournalId,
            ["currency_id"] = ctx.CurrencyId,
            ["ref"] = (JsonNode?)(string.IsNullOrWhiteSpace(doc.NumAtCard) ? null : doc.NumAtCard.Trim()),
            ["invoice_date"] = OdooValues.Date(doc.DocDate),
            ["date"] = OdooValues.Date(doc.DocDate),
            ["x_sap_docnum"] = doc.DocNum.ToString(CultureInfo.InvariantCulture),
            ["x_sap_objtype"] = doc.ObjType.ToString(CultureInfo.InvariantCulture),
        };

        if (doc.DocDueDate.HasValue)
            values["invoice_date_due"] = OdooValues.Date(doc.DocDueDate.Value);

        if (!string.IsNullOrWhiteSpace(doc.Comments))
            values["narration"] = doc.Comments.Trim();

        if (doc.IsCreditMemo)
        {
            values[CreditDocEntryField] = doc.DocEntry;
            if (ctx.ReversedEntryId.HasValue)
                values["reversed_entry_id"] = ctx.ReversedEntryId.Value;
        }
        else
        {
            values[InvoiceDocEntryField] = doc.DocEntry;
        }

        if (foreign && doc.DocRate > 0)
        {
            decimal rate = ctx.InvoiceRateIsInverseOfDocRate ? 1m / doc.DocRate : doc.DocRate;
            values["invoice_currency_rate"] = (double)Math.Round(rate, 12);
        }

        var lineCommands = new JsonArray();
        foreach (var line in lines)
        {
            var lineValues = new JsonObject
            {
                ["name"] = line.Name,
                ["quantity"] = (double)line.Quantity,
                ["price_unit"] = (double)line.PriceUnit,
                ["account_id"] = line.AccountId,
                // Always explicit: an empty set stops Odoo from applying the account's default taxes.
                ["tax_ids"] = new JsonArray { new JsonArray { 6, 0, line.TaxId.HasValue ? new JsonArray { line.TaxId.Value } : new JsonArray() } },
            };
            if (line.AnalyticAccountId.HasValue)
                lineValues["analytic_distribution"] = new JsonObject
                {
                    [line.AnalyticAccountId.Value.ToString(CultureInfo.InvariantCulture)] = 100
                };

            lineCommands.Add(new JsonArray { 0, 0, lineValues });
        }
        values["invoice_line_ids"] = lineCommands;

        return values;
    }

    private static decimal Round6(decimal v) => Math.Round(v, 6, MidpointRounding.AwayFromZero);
}
