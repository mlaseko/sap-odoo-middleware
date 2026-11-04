namespace SapOdooMiddleware.Models.PurchaseSync;

/// <summary>
/// Body of <c>POST /api/ap-invoices/draft</c>, sent by the control centre when "Create SAP A/P invoice" is pressed on
/// an Odoo container order. The supplier invoice as Odoo holds it (every line, free goods, freight, display items);
/// the middleware builds a SAP A/P invoice <b>draft</b> on the container's GRPO from it. Snake_case on the wire.
/// </summary>
public sealed class ApInvoiceDraftRequest
{
    /// <summary>Odoo <c>supplier.order</c> id, echoed for the write-back.</summary>
    public int? OdooOrderId { get; set; }

    /// <summary>The container order's reference (<c>supplier.order.name</c>, e.g. A12423210).</summary>
    public string OdooShipmentId { get; set; } = string.Empty;

    /// <summary>Supplier CardCode (S00001 for Liqui Moly).</summary>
    public string CardCode { get; set; } = string.Empty;

    /// <summary>Supplier invoice number → <c>NumAtCard</c>; also the duplicate check.</summary>
    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>Supplier invoice date → document date (<c>TaxDate</c>) and the date of the exchange rate used.</summary>
    public DateTime InvoiceDate { get; set; }

    /// <summary>Payment due date → <c>DocDueDate</c>; the posting date when absent.</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>Posting date (<c>DocDate</c>); today when absent. Kept on or after the Odoo cut-over so the posted
    /// invoice syncs back as the Odoo bill.</summary>
    public DateTime? PostingDate { get; set; }

    /// <summary>Invoice currency (EUR).</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>The container's goods receipt (OPDN.DocEntry): every item line of the draft is based on it.</summary>
    public int GrpoDocentry { get; set; }

    /// <summary>Freight on the invoice, in the invoice currency.</summary>
    public double FreightFc { get; set; }

    /// <summary>The supplier invoice total (products + freight), in the invoice currency. The draft must equal it.</summary>
    public double InvoiceTotalFc { get; set; }

    /// <summary>OEXD code of the "Liqui Moly freight" additional expense (stock-relevant).</summary>
    public int? FreightExpenseCode { get; set; }

    /// <summary>OEXD code of the "Liqui Moly display/marketing" additional expense (6011523, not stock).</summary>
    public int? MarketingExpenseCode { get; set; }

    /// <summary>Largest difference allowed between the draft total and <see cref="InvoiceTotalFc"/>. Default 0.05.</summary>
    public double Tolerance { get; set; } = 0.05;

    public string? Remarks { get; set; }

    /// <summary>The invoice lines, as on the supplier invoice.</summary>
    public List<ApInvoiceDraftLineRequest> Lines { get; set; } = new();

    /// <summary>Set by the control centre job; ignored here.</summary>
    public string? SapPurchaseJob { get; set; }
}

/// <summary>One supplier invoice line.</summary>
public sealed class ApInvoiceDraftLineRequest
{
    public string ItemCode { get; set; } = string.Empty;
    public string? Name { get; set; }
    public double Quantity { get; set; }

    /// <summary>List price per unit, in the invoice currency.</summary>
    public double UnitPrice { get; set; }

    /// <summary>Discount in percent (100 on a free line).</summary>
    public double DiscountPercent { get; set; }

    /// <summary>Line amount after discount, in the invoice currency.</summary>
    public double Amount { get; set; }

    /// <summary>A free (100% discount) line.</summary>
    public bool IsFree { get; set; }
}

/// <summary>Result of the draft creation.</summary>
public sealed class ApInvoiceDraftResult
{
    /// <summary>ODRF.DocEntry of the draft (or of the existing draft / posted invoice when one already exists).</summary>
    public int DocEntry { get; set; }

    public int DocNum { get; set; }

    /// <summary>"draft" for a new or existing draft, "invoice" when a posted A/P invoice with this number exists.</summary>
    public string Kind { get; set; } = "draft";

    public bool AlreadyExisted { get; set; }

    /// <summary>SAP's total of the draft, invoice currency.</summary>
    public double DocTotalFc { get; set; }

    /// <summary>What the plan expected (item lines + display items + freight).</summary>
    public double ExpectedTotalFc { get; set; }

    public double DocRate { get; set; }
    public int ItemLines { get; set; }
    public double FreightFc { get; set; }
    public double MarketingFc { get; set; }

    /// <summary>Notes for the reviewer: free items with no stock line, lines matched across a price difference.</summary>
    public List<string> Notes { get; set; } = new();
}

/// <summary>A goods receipt line (PDN1) as read for the draft.</summary>
public sealed record ApGrpoLine(
    int LineNum, string ItemCode, double Quantity, double OpenQuantity, double Price, double DiscountPercent, string LineStatus);

/// <summary>A planned A/P invoice item line: based on a GRPO line, priced as on the supplier invoice.</summary>
public sealed record ApDraftItemLine(
    int BaseLine, string ItemCode, double Quantity, double UnitPrice, double DiscountPercent, decimal LineTotal);

/// <summary>The planned draft.</summary>
public sealed record ApDraftPlan(
    IReadOnlyList<ApDraftItemLine> ItemLines,
    decimal ItemTotal,
    decimal MarketingTotal,
    IReadOnlyList<string> MarketingItems,
    decimal Freight,
    decimal ExpectedTotal,
    IReadOnlyList<string> Notes);

/// <summary>
/// The draft cannot be built from what Odoo sent and what SAP holds (unmatched line, quantity difference, total
/// difference, invoice already in SAP). Mapped to HTTP 422; nothing is left in SAP.
/// </summary>
public sealed class ApInvoiceGuardException : Exception
{
    public string Code { get; }

    public ApInvoiceGuardException(string code, string message) : base($"{code}: {message}")
    {
        Code = code;
    }
}
