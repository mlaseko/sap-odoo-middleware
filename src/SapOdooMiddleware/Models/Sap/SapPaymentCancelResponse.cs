namespace SapOdooMiddleware.Models.Sap;

/// <summary>Result of cancelling an Incoming Payment (ORCT).</summary>
public class SapPaymentCancelResponse
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    /// <summary>True when the payment was already cancelled — no new SAP action was taken.</summary>
    public bool AlreadyCancelled { get; set; }
}
