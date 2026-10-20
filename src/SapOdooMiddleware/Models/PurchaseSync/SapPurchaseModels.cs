namespace SapOdooMiddleware.Models.PurchaseSync;

/// <summary>Purchase Order header (OPOR) as read for <c>PO_UPSERT</c>.</summary>
public sealed record SapPoHeader(
    int DocEntry,
    int DocNum,
    string CardCode,
    string? CardName,
    string? NumAtCard,
    string DocCur,
    decimal DocRate,
    decimal DocTotal,
    decimal DocTotalFc,
    DateTime DocDate,
    DateTime? DocDueDate,
    string DocStatus,
    string Canceled)
{
    /// <summary>open / closed / cancelled, the value written to <c>x.sap.po.status</c>.</summary>
    public string Status => Canceled == "Y" || Canceled == "C"
        ? "cancelled"
        : DocStatus == "C" ? "closed" : "open";
}

/// <summary>Goods Receipt PO header (OPDN) with its lines, as read for <c>GRPO_LINK</c>.</summary>
public sealed record SapGrpoHeader(
    int DocEntry,
    int DocNum,
    string CardCode,
    DateTime DocDate,
    decimal DocTotal,
    string? ShipRef,
    string? OdooShipmentId,
    string Canceled,
    IReadOnlyList<SapGrpoLine> Lines)
{
    public IReadOnlyList<int> PoDocEntries =>
        Lines.Where(l => l.BaseType == 22 && l.BaseEntry.HasValue)
             .Select(l => l.BaseEntry!.Value).Distinct().ToList();
}

public sealed record SapGrpoLine(
    int LineNum,
    string ItemCode,
    string? Dscription,
    decimal Quantity,
    int? BaseType,
    int? BaseEntry,
    int? BaseLine);

/// <summary>
/// A/P Invoice (OPCH, ObjType 18) or A/P Credit Memo (ORPC, ObjType 19) with item lines and
/// additional-expense (freight) lines.
/// </summary>
public sealed record SapApDocument(
    int ObjType,
    int DocEntry,
    int DocNum,
    string CardCode,
    string? CardName,
    string? NumAtCard,
    string DocCur,
    decimal DocRate,
    decimal DocTotal,
    decimal DocTotalFc,
    DateTime DocDate,
    DateTime? DocDueDate,
    DateTime? TaxDate,
    string Canceled,
    string DocType,
    string? Comments,
    IReadOnlyList<SapApLine> Lines,
    IReadOnlyList<SapApExpense> Expenses,
    IReadOnlyList<int> PoDocEntries)
{
    public bool IsCreditMemo => ObjType == 19;
    public bool IsServiceType => string.Equals(DocType, "S", StringComparison.OrdinalIgnoreCase);
    public bool IsCancellationDocument => Canceled == "C";

    /// <summary>DocEntry of the A/P invoice a credit memo was drawn from, when every line is based on one.</summary>
    public int? BaseInvoiceDocEntry
    {
        get
        {
            var bases = Lines.Where(l => l.BaseType == 18 && l.BaseEntry.HasValue)
                             .Select(l => l.BaseEntry!.Value).Distinct().ToList();
            return bases.Count == 1 ? bases[0] : null;
        }
    }
}

/// <summary>
/// One PCH1 / RPC1 line. <see cref="Price"/> is the unit price after discount in the document
/// currency; <see cref="LineTotal"/> is in local currency and <see cref="TotalFrgn"/> in the
/// document currency (0 when the document is in local currency).
/// </summary>
public sealed record SapApLine(
    int LineNum,
    string? ItemCode,
    string? Dscription,
    decimal Quantity,
    decimal Price,
    decimal LineTotal,
    decimal TotalFrgn,
    string? VatGroup,
    string? AcctCode,
    int? ItmsGrpCod,
    int? BaseType,
    int? BaseEntry,
    int? BaseLine);

/// <summary>One PCH3 / RPC3 additional-expense line (freight etc.).</summary>
public sealed record SapApExpense(
    int ExpnsCode,
    string? ExpnsName,
    decimal LineTotal,
    decimal TotalFrgn,
    string? VatGroup,
    string? ExpenseAccount);

/// <summary>One candidate document for the backfill (spec v4 §6.5).</summary>
public sealed record SapBackfillCandidate(
    string ObjectType,
    string EventType,
    int DocEntry,
    int DocNum,
    string CardCode,
    string? NumAtCard,
    DateTime DocDate,
    decimal DocTotal,
    string DocCur,
    string Canceled);
