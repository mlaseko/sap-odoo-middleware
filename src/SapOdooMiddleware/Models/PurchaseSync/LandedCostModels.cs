namespace SapOdooMiddleware.Models.PurchaseSync;

/// <summary>
/// Body of <c>POST /api/landed-costs</c> (spec v4 §6.3), sent by the control centre's landed-cost job when a
/// container order reaches "Costs final". Snake_case on the wire.
/// </summary>
public sealed class LandedCostPushRequest
{
    /// <summary>Odoo <c>supplier.order</c> id, echoed for the write-back.</summary>
    public int? OdooOrderId { get; set; }

    /// <summary>The container order's reference (<c>supplier.order.name</c>). With <see cref="PushSequence"/> it forms
    /// the idempotency key written to <c>OIPF.U_OdooShipmentId</c>: the reference itself for the first push, <c>"&lt;ref&gt;:&lt;n&gt;"</c>
    /// for a delta.</summary>
    public string OdooShipmentId { get; set; } = string.Empty;

    /// <summary>Container number → <c>OIPF.U_ShipRef</c> and the document's <c>Reference</c>.</summary>
    public string? ShipRef { get; set; }

    /// <summary>The container's goods receipt (OPDN.DocEntry). Its lines are the item lines of the first push.</summary>
    public int GrpoDocentry { get; set; }

    /// <summary>Set for a delta push: the last SAP Landed Costs document of this container. The new document is then
    /// based on that document's lines (base type 69) instead of the GRPO's, so SAP adds the delta to the same items.</summary>
    public int? PreviousLcDocentry { get; set; }

    /// <summary>1 for the first push of the container, 2 for the first delta, and so on (the count of SAP landed cost
    /// documents the order already carries, plus one). Defaults to 1 when absent or 0.</summary>
    public int PushSequence { get; set; } = 1;

    /// <summary>A negative total (a credit note on a cost bill after the push) is refused unless this is true. The
    /// control centre sets it from the order's "negative landed cost approved" tick.</summary>
    public bool ApproveNegative { get; set; }

    /// <summary>Posting date; today when absent.</summary>
    public DateTime? DocDate { get; set; }

    public string? Remarks { get; set; }

    /// <summary>One line per SAP landed cost code, in local currency (TZS). Lines with the same code are merged; zero
    /// lines are dropped.</summary>
    public List<LandedCostLineRequest> CostLines { get; set; } = new();

    /// <summary>Set by the control centre job; ignored here.</summary>
    public string? SapPurchaseJob { get; set; }
}

/// <summary>One cost line: a SAP landed cost code (OALC.AlcCode) and its amount in local currency.</summary>
public sealed class LandedCostLineRequest
{
    /// <summary>SAP landed cost code (<c>OALC.AlcCode</c>, text; <c>supplier.expense.category.x_sap_lc_code</c>).</summary>
    public string LcCode { get; set; } = string.Empty;

    /// <summary>Amount in local currency. Negative only in a delta push (and only with <c>approve_negative</c>).</summary>
    public double AmountTzs { get; set; }

    /// <summary>How SAP spreads the cost over the item lines: <c>cash_value_before</c> (default), <c>cash_value_after</c>,
    /// <c>quantity</c>, <c>weight</c>, <c>volume</c> or <c>equally</c>.</summary>
    public string? AllocationBy { get; set; }

    /// <summary>Informational: the Odoo category name.</summary>
    public string? Label { get; set; }
}

/// <summary>Result of the landed cost push.</summary>
public sealed class LandedCostPushResult
{
    /// <summary>OIPF.DocEntry.</summary>
    public int DocEntry { get; set; }

    /// <summary>OIPF.DocNum.</summary>
    public int DocNum { get; set; }

    /// <summary>What was written to <c>U_OdooShipmentId</c> (<c>ref</c> or <c>ref:n</c>).</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Number of item lines (GRPO or previous landed cost lines) the document is based on.</summary>
    public int ItemLines { get; set; }

    /// <summary>Sum of the cost lines actually posted, local currency.</summary>
    public double CostTotal { get; set; }

    /// <summary>True when a document with this key already existed; nothing was created.</summary>
    public bool AlreadyExisted { get; set; }
}

/// <summary>
/// A guard of spec §6.3 refused the push before SAP was touched (no GRPO, negative total without approval, unknown
/// landed cost code, previous document not this container's). Mapped to HTTP 422; the job fails with the code and
/// message and is not retried automatically.
/// </summary>
public sealed class LandedCostGuardException : Exception
{
    public string Code { get; }

    public LandedCostGuardException(string code, string message) : base($"{code}: {message}")
    {
        Code = code;
    }
}
