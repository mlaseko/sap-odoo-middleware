using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Models.PurchaseSync;

/// <summary>
/// Body of <c>POST /api/outgoing-payments</c> and <c>POST /api/supplier-refunds</c> (spec v4 §6.2), sent by the
/// control centre's purchases jobs. Snake_case on the wire (<c>odoo_pay_ref</c>, <c>card_code</c>, …).
/// </summary>
public sealed class SupplierPaymentRequest
{
    /// <summary>Odoo <c>account.payment</c> id, echoed for the write-back.</summary>
    public int? OdooPaymentId { get; set; }

    /// <summary>Odoo payment name → <c>U_OdooPayRef</c>, the idempotency key (50 chars).</summary>
    public string OdooPayRef { get; set; } = string.Empty;

    /// <summary>SAP business partner (OCRD.CardCode) of the supplier.</summary>
    public string CardCode { get; set; } = string.Empty;

    public DateTime DocDate { get; set; }

    /// <summary>ISO currency of the payment (EUR).</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>SAP <c>DocRate</c>: local-currency units per document-currency unit (TZS per EUR). 1 for TZS.</summary>
    public double Rate { get; set; }

    /// <summary>Amount in the document currency (<c>TransferSum</c>). Always positive.</summary>
    public double Amount { get; set; }

    /// <summary>SAP bank G/L account (<c>TransferAccount</c>), from <c>sap.journal.mapping</c>.</summary>
    public string SapAccountCode { get; set; } = string.Empty;

    /// <summary>SWIFT / bank reference (<c>TransferReference</c>).</summary>
    public string? TransferReference { get; set; }

    public string? Remarks { get; set; }

    /// <summary>Informational: "46" outgoing, "24" incoming. The route decides, not this field.</summary>
    public string? SapObjtype { get; set; }

    /// <summary>Set by the control centre job; ignored here.</summary>
    public string? SapPurchaseJob { get; set; }

    /// <summary>True for <c>/api/supplier-refunds</c> (an Incoming Payment from the vendor). Set by the controller.</summary>
    [JsonIgnore]
    public bool IsRefund { get; set; }
}

/// <summary>Result of an on-account supplier payment or refund.</summary>
public sealed class SupplierPaymentResult
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }

    /// <summary>"46" (OVPM) or "24" (ORCT).</summary>
    public string ObjType { get; set; } = "46";

    /// <summary>True when a payment with this <c>U_OdooPayRef</c> already existed; nothing was created.</summary>
    public bool AlreadyExisted { get; set; }
}

/// <summary>Body of <c>POST …/{doc_entry}/cancel</c>. Everything is informational.</summary>
public sealed class SupplierPaymentCancelRequest
{
    public int? OdooPaymentId { get; set; }
    public string? OdooPayRef { get; set; }
    public string? Reason { get; set; }
    public string? SapPurchaseJob { get; set; }
}

/// <summary>Result of cancelling an on-account supplier payment or refund.</summary>
public sealed class SupplierPaymentCancelResult
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public bool Cancelled { get; set; } = true;

    /// <summary>True when SAP already showed the payment as cancelled; no new SAP action was taken.</summary>
    public bool AlreadyCancelled { get; set; }
}

/// <summary>Body of <c>POST /api/supplier-adjustments</c> (spec v4 §6.2): a journal entry on the business partner.</summary>
public sealed class SupplierAdjustmentRequest
{
    /// <summary>Odoo <c>supplier.account.entry</c> id, echoed for the write-back.</summary>
    public int? OdooEntryId { get; set; }

    /// <summary>"SAE-&lt;entry id&gt;" → <c>U_OdooRef</c>, the idempotency key (50 chars).</summary>
    public string OdooRef { get; set; } = string.Empty;

    public string CardCode { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public string Currency { get; set; } = string.Empty;

    /// <summary>Local-currency units per document-currency unit (TZS per EUR). 1 for TZS.</summary>
    public double Rate { get; set; }

    /// <summary>Always positive; <see cref="Direction"/> carries the side.</summary>
    public double Amount { get; set; }

    /// <summary><c>credit_supplier</c> (Cr BP / Dr G/L) or <c>debit_supplier</c> (Dr BP / Cr G/L).</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary><c>bank_charges</c> or <c>small_balance</c>; informational (the memo).</summary>
    public string? Reason { get; set; }

    /// <summary>The mirror G/L account (<c>icc.sap.adjustment.map.sap_gl_code</c>).</summary>
    public string SapGlCode { get; set; } = string.Empty;

    public string? Memo { get; set; }
    public string? SapPurchaseJob { get; set; }
}

/// <summary>Result of the adjustment journal entry. <c>JdtDocentry</c> is OJDT.TransId.</summary>
public sealed class SupplierAdjustmentResult
{
    public int JdtDocentry { get; set; }

    /// <summary>OJDT.Number, the number users see in SAP.</summary>
    public int Number { get; set; }

    public bool AlreadyExisted { get; set; }
}

/// <summary>Body of <c>POST /api/supplier-adjustments/{jdt_docentry}/reverse</c>. Informational.</summary>
public sealed class AdjustmentReverseRequest
{
    public int? OdooEntryId { get; set; }
    public string? OdooRef { get; set; }
    public string? SapPurchaseJob { get; set; }
}

/// <summary>Result of reversing (storno) an adjustment journal entry.</summary>
public sealed class AdjustmentReverseResult
{
    public int OriginalDocentry { get; set; }
    public int ReversalDocentry { get; set; }
    public int ReversalNumber { get; set; }
    public bool AlreadyExisted { get; set; }
}

/// <summary>
/// The request conflicts with what SAP already holds (e.g. the idempotency key belongs to a payment that was
/// cancelled in SAP). Mapped to HTTP 409; the control centre job fails and is not retried automatically.
/// </summary>
public sealed class SapConflictException : Exception
{
    public SapConflictException(string message) : base(message) { }
}
