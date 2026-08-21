namespace SapOdooMiddleware.Models.Inventory;

// ── Open AR invoice lines (return-request copy screen) ───────────────

/// <summary>An AR invoice line eligible to copy onto a Return Request (OINV/INV1 + OITM).</summary>
public class OpenInvoiceLine
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
    public string? Manufacturer { get; set; }
    public double Quantity { get; set; }
    /// <summary>Unit price on the invoice line (after discount).</summary>
    public double Price { get; set; }
    public string WhsCode { get; set; } = "";
}

// ── Return Request (ORRR, copy from AR invoice) ──────────────────────

/// <summary>
/// POST /api/autohub/inv/return-requests body. Creates a Return Request (ORRR)
/// with every line copied from one of the customer's AR invoice lines (base refs
/// keep prices and the document chain intact). No stock moves yet.
/// </summary>
public class ReturnRequestCreate
{
    /// <summary>App-generated GUID for idempotency (written to U_AppRef, max 40 chars).</summary>
    public string AppRef { get; set; } = "";
    /// <summary>Customer CardCode — must match the base invoices.</summary>
    public string CardCode { get; set; } = "";
    public DateTime? DocDate { get; set; }
    public string? Comments { get; set; }
    public List<ReturnRequestLineCreate> Lines { get; set; } = new();
}

public class ReturnRequestLineCreate
{
    /// <summary>Base AR invoice DocEntry (OINV).</summary>
    public int InvoiceDocEntry { get; set; }
    /// <summary>Base AR invoice LineNum (INV1).</summary>
    public int InvoiceLineNum { get; set; }
    /// <summary>Quantity being returned (≤ the invoiced quantity).</summary>
    public double Quantity { get; set; }
    /// <summary>Warehouse override; defaults to the invoice line's warehouse.</summary>
    public string? WhsCode { get; set; }
}

// ── Open return request lines (goods-return copy screen) ─────────────

/// <summary>Open Return Request line awaiting the physical return (ORRR/RRR1 + OITM).</summary>
public class OpenReturnRequestLine
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
    public string? Manufacturer { get; set; }
    public double Quantity { get; set; }
    public double OpenQty { get; set; }
    public string WhsCode { get; set; } = "";
}

// ── Goods Return (ORDN, copy from Return Request) ────────────────────

/// <summary>
/// POST /api/autohub/inv/returns body. Creates the Goods Return (ORDN) from open
/// Return Request lines — base refs close the request's open quantities and the
/// stock comes back into the warehouse (destination bins resolve like receipts).
/// </summary>
public class GoodsReturnCreate
{
    /// <summary>App-generated GUID for idempotency (written to U_AppRef, max 40 chars).</summary>
    public string AppRef { get; set; } = "";
    /// <summary>Customer CardCode — must match the base return requests.</summary>
    public string CardCode { get; set; } = "";
    public DateTime? DocDate { get; set; }
    public string? Comments { get; set; }
    public List<GoodsReturnLineCreate> Lines { get; set; } = new();
}

// ── Listings / cancel ────────────────────────────────────────────────

/// <summary>Customer picker row (OCRD, CardType C, not frozen).</summary>
public class CustomerSummary
{
    public string CardCode { get; set; } = "";
    public string? CardName { get; set; }
    public string? Phone { get; set; }
}

/// <summary>
/// Return document header with status — used for both Return Requests (ORRR) and
/// Goods Returns (ORDN). Status: "open" | "closed" | "canceled".
/// </summary>
public class ReturnDocumentSummary
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string DocDate { get; set; } = "";
    public string CardCode { get; set; } = "";
    public string? CardName { get; set; }
    public string Status { get; set; } = "";
    public int TotalLines { get; set; }
    public double TotalQty { get; set; }
    /// <summary>Remaining open quantity across lines (0 when fully drawn/closed).</summary>
    public double OpenQty { get; set; }
}

/// <summary>Result of cancelling a document (idempotent on already-cancelled).</summary>
public class DocCancelResult
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public bool AlreadyCancelled { get; set; }
}

public class GoodsReturnLineCreate
{
    /// <summary>Base Return Request DocEntry (ORRR).</summary>
    public int ReturnRequestDocEntry { get; set; }
    /// <summary>Base Return Request LineNum (RRR1).</summary>
    public int ReturnRequestLineNum { get; set; }
    /// <summary>Quantity physically returned now (≤ the request line's open quantity).</summary>
    public double Quantity { get; set; }
    /// <summary>Receiving warehouse override; defaults to the request line's warehouse.</summary>
    public string? WhsCode { get; set; }
    /// <summary>Destination bin (OBIN AbsEntry). Required when the receiving warehouse
    /// is bin-managed and the resolver cannot auto-select.</summary>
    public int? BinAbs { get; set; }
}
