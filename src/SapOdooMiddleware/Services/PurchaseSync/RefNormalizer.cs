namespace SapOdooMiddleware.Services.PurchaseSync;

/// <summary>
/// Normalises a supplier invoice number or container number for matching (spec v4 §5.3):
/// trimmed, upper-case, no whitespace. <c>"LM 12345 / 1"</c> → <c>"LM12345/1"</c>.
/// </summary>
public static class RefNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var chars = value.Where(c => !char.IsWhiteSpace(c)).ToArray();
        return new string(chars).ToUpperInvariant();
    }

    public static bool Matches(string? a, string? b)
    {
        var na = Normalize(a);
        return na.Length > 0 && na == Normalize(b);
    }
}

/// <summary>
/// A failure the purchases worker understands. <see cref="Retryable"/> = false means the row is
/// marked <c>failed</c> at once (a person fixes a mapping and retries from the queue view);
/// true means the normal retry/backoff applies (SAP SQL or Odoo unreachable).
/// </summary>
public sealed class PurchaseSyncException : Exception
{
    public string Code { get; }
    public bool Retryable { get; }

    public PurchaseSyncException(string code, string message, bool retryable = false)
        : base($"{code}: {message}")
    {
        Code = code;
        Retryable = retryable;
    }
}

/// <summary>The six purchase EventType values carried in ODOO_WEBHOOK_QUEUE (spec v4 §4.2).</summary>
public static class PurchaseEventTypes
{
    public const string PoUpsert = "PO_UPSERT";
    public const string GrpoLink = "GRPO_LINK";
    public const string ApInvoiceCreate = "AP_INVOICE_CREATE";
    public const string ApInvoiceCancel = "AP_INVOICE_CANCEL";
    public const string ApCreditCreate = "AP_CREDIT_CREATE";
    public const string ApCreditCancel = "AP_CREDIT_CANCEL";

    public static readonly string[] All =
    [
        PoUpsert, GrpoLink, ApInvoiceCreate, ApInvoiceCancel, ApCreditCreate, ApCreditCancel
    ];

    /// <summary>SAP ObjType → the EventType for an Add ('A') transaction, or null when not synced.</summary>
    public static string? ForAdd(string objectType) => objectType switch
    {
        "22" => PoUpsert,
        "20" => GrpoLink,
        "18" => ApInvoiceCreate,
        "19" => ApCreditCreate,
        _ => null
    };

    public static bool IsPurchaseEvent(string? eventType) =>
        eventType != null && Array.IndexOf(All, eventType) >= 0;
}
