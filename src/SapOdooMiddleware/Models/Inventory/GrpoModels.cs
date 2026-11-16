namespace SapOdooMiddleware.Models.Inventory;

// ── Open PO lines (receiving screen) ─────────────────────────────────

/// <summary>Open purchase order line awaiting receipt (OPOR/POR1 + OITM).</summary>
public class OpenPurchaseOrderLine
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string DocDate { get; set; } = "";
    public string CardCode { get; set; } = "";
    public string? CardName { get; set; }
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = "";
    public string? ItemName { get; set; }
    public string? ArticleNumber { get; set; }
    /// <summary>Supplier's catalog (article) number on the PO line (POR1.VendorNum).</summary>
    public string? SupplierCatNum { get; set; }
    public string? Manufacturer { get; set; }
    public double Quantity { get; set; }
    public double OpenQty { get; set; }
    /// <summary>Warehouse on the PO line — the default receiving destination.</summary>
    public string WhsCode { get; set; } = "";
    /// <summary>Currency of the purchase order (OPOR.DocCur).</summary>
    public string DocCurrency { get; set; } = "";
    /// <summary>Exchange rate of the purchase order (OPOR.DocRate; 1 for local currency).</summary>
    public double DocRate { get; set; }
}

// ── GRPO (OPDN, copy from PO) ────────────────────────────────────────

/// <summary>
/// POST /api/autohub/inv/grpo body. Goods Receipt PO (OPDN, object 20) drawn from
/// open purchase order lines — base refs update the PO's open quantities and vendor
/// liability. Partial receipts allowed; SAP closes fully received PO lines.
/// </summary>
public class GrpoCreate
{
    /// <summary>App-generated GUID for idempotency (written to U_AppRef, max 40 chars).</summary>
    public string AppRef { get; set; } = "";
    /// <summary>Vendor CardCode — must match the base purchase orders.</summary>
    public string CardCode { get; set; } = "";
    public DateTime? DocDate { get; set; }
    /// <summary>Vendor reference (OPDN.NumAtCard), e.g. the supplier invoice and cartons. Max 100 chars.</summary>
    public string? NumAtCard { get; set; }
    /// <summary>Remarks (OPDN.Comments). Max 254 chars.</summary>
    public string? Comments { get; set; }
    public List<GrpoLineCreate> Lines { get; set; } = new();
}

/// <summary>
/// Currency and exchange rate a GRPO takes from its base purchase orders. Every line of one GRPO
/// must share them; for a foreign-currency PO the GRPO's DocRate is set to the PO's.
/// </summary>
public sealed record PurchaseDocCurrency(string Currency, double Rate);

public class GrpoLineCreate
{
    /// <summary>Base purchase order DocEntry (OPOR).</summary>
    public int PoDocEntry { get; set; }
    /// <summary>Base purchase order LineNum (POR1).</summary>
    public int PoLineNum { get; set; }
    /// <summary>Quantity received now (≤ the PO line's open quantity).</summary>
    public double Quantity { get; set; }
    /// <summary>Receiving warehouse override; defaults to the PO line's warehouse.</summary>
    public string? WhsCode { get; set; }
    /// <summary>Destination bin (OBIN AbsEntry). Required when the receiving warehouse
    /// is bin-managed and the resolver cannot auto-select.</summary>
    public int? BinAbs { get; set; }
}

// ── A/P invoice draft (ODRF, ObjType 18, copy from GRPOs) ────────────

/// <summary>
/// POST /api/autohub/inv/purchase-invoice-drafts body. One A/P invoice draft drawn from every
/// open line of several GRPOs of one supplier (a shipment received carton by carton). The draft
/// is never added as an invoice here: finance reviews and adds it in SAP.
/// </summary>
public class PurchaseInvoiceDraftCreate
{
    /// <summary>App-generated id for idempotency (written to ODRF.U_AppRef, max 40 chars).</summary>
    public string AppRef { get; set; } = "";
    /// <summary>Supplier CardCode — every GRPO must belong to it.</summary>
    public string CardCode { get; set; } = "";
    /// <summary>Supplier invoice number → NumAtCard (max 100 chars). Unique per supplier.</summary>
    public string NumAtCard { get; set; } = "";
    /// <summary>Posting date (DocDate); today when absent.</summary>
    public DateTime? DocDate { get; set; }
    /// <summary>Supplier invoice date (TaxDate); SAP's default when absent.</summary>
    public DateTime? TaxDate { get; set; }
    /// <summary>Remarks (Comments, max 254 chars).</summary>
    public string? Comments { get; set; }
    /// <summary>The GRPOs (OPDN.DocEntry) whose open lines make up the draft.</summary>
    public List<int> GrpoDocEntries { get; set; } = new();
    /// <summary>Optional supplier invoice total in the document currency. When SAP's draft total
    /// differs by more than 0.01, the draft is deleted and the request refused (422).</summary>
    public double? ExpectedTotal { get; set; }
}

/// <summary>Response <c>data</c> of POST /api/autohub/inv/purchase-invoice-drafts.</summary>
public class PurchaseInvoiceDraftResult
{
    public int DraftDocEntry { get; set; }
    public int DraftDocNum { get; set; }
    public int LineCount { get; set; }
    /// <summary>Draft total in the document currency (DocTotalFC for foreign currency, else DocTotal).</summary>
    public double DocTotal { get; set; }
    public string DocCurrency { get; set; } = "";
    public bool AlreadyExisted { get; set; }
}

/// <summary>A GRPO named in a draft request, with its open lines (OPDN/PDN1).</summary>
public class GrpoForInvoice
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string CardCode { get; set; } = "";
    /// <summary>OPDN.DocStatus: O = open, C = closed.</summary>
    public string DocStatus { get; set; } = "";
    /// <summary>OPDN.CANCELED: N, Y (cancelled) or C (a cancellation document).</summary>
    public string Canceled { get; set; } = "N";
    public string DocCurrency { get; set; } = "";
    public double DocRate { get; set; }
    /// <summary>Branch of the GRPO (OPDN.BPLId); null when the company has no branches.</summary>
    public int? BplId { get; set; }
    public List<GrpoOpenLine> OpenLines { get; set; } = new();
}

/// <summary>An open GRPO line still to be invoiced.</summary>
public sealed record GrpoOpenLine(int LineNum, double OpenQty);

/// <summary>An A/P invoice ("invoice") or open A/P invoice draft ("draft") of the supplier carrying the
/// same supplier invoice number.</summary>
public sealed record PurchaseInvoiceConflict(string Kind, int DocEntry, int DocNum, string? AppRef);

/// <summary>
/// SAP's draft total differed from <see cref="PurchaseInvoiceDraftCreate.ExpectedTotal"/>; the draft was
/// deleted (unless <see cref="Removed"/> is false), so nothing stays in SAP.
/// </summary>
public sealed class PurchaseInvoiceDraftTotalMismatchException : Exception
{
    public double SapTotal { get; }
    public double ExpectedTotal { get; }
    public bool Removed { get; }

    public PurchaseInvoiceDraftTotalMismatchException(
        int draftDocEntry, double sapTotal, double expectedTotal, string currency, bool removed)
        : base($"SAP computed {sapTotal:0.00} {currency} for the draft but the expected total is " +
               $"{expectedTotal:0.00} {currency}; draft {draftDocEntry} " +
               (removed ? "was deleted." : "could NOT be deleted — delete it in SAP."))
    {
        SapTotal = sapTotal;
        ExpectedTotal = expectedTotal;
        Removed = removed;
    }
}

/// <summary>
/// GET /api/autohub/inv/purchase-invoice-drafts/{draftDocEntry}: what became of an A/P invoice draft.
/// <c>Status</c>: <c>open</c> (waiting for finance), <c>added</c> (finance added it: the A/P invoice whose
/// OPCH.draftKey is the draft), <c>closed</c> (closed in SAP without an invoice found), <c>deleted</c> (no
/// such draft any more and no invoice from it).
/// </summary>
public class PurchaseInvoiceDraftStatus
{
    public int DraftDocEntry { get; set; }
    public string Status { get; set; } = "";
    public int? DraftDocNum { get; set; }
    public string? CardCode { get; set; }
    public string? NumAtCard { get; set; }
    public string? AppRef { get; set; }
    public double? DocTotal { get; set; }
    public string? DocCurrency { get; set; }
    public int? InvoiceDocEntry { get; set; }
    public int? InvoiceDocNum { get; set; }
    /// <summary>True when the A/P invoice added from the draft has since been cancelled.</summary>
    public bool InvoiceCancelled { get; set; }
}
