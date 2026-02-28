using System.ComponentModel.DataAnnotations;

namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Unified re-sync request payload.  The caller specifies the document type
/// and SAP DocEntry, plus the matching sub-payload for that document type.
///
/// Supported <c>document_type</c> values:
/// <list type="bullet">
///   <item><c>sales_order</c> — re-syncs ORDR UDFs via <see cref="SalesOrder"/></item>
///   <item><c>invoice</c> — re-syncs OINV UDFs via <see cref="Invoice"/></item>
///   <item><c>incoming_payment</c> — re-syncs ORCT UDFs via <see cref="IncomingPayment"/></item>
///   <item><c>credit_memo</c> — re-syncs ORIN UDFs via <see cref="CreditMemo"/></item>
///   <item><c>goods_return</c> — re-syncs ORDN UDFs via <see cref="GoodsReturn"/></item>
/// </list>
/// </summary>
public class ResyncRequest
{
    /// <summary>
    /// SAP document type to re-sync.
    /// One of: <c>sales_order</c>, <c>invoice</c>, <c>incoming_payment</c>, <c>credit_memo</c>, <c>goods_return</c>.
    /// </summary>
    [Required]
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>
    /// SAP DocEntry of the document to update.
    /// </summary>
    [Required]
    public int DocEntry { get; set; }

    /// <summary>
    /// Payload for Sales Order re-sync (required when document_type = "sales_order").
    /// </summary>
    public SapSalesOrderRequest? SalesOrder { get; set; }

    /// <summary>
    /// Payload for AR Invoice re-sync (required when document_type = "invoice").
    /// </summary>
    public SapInvoiceRequest? Invoice { get; set; }

    /// <summary>
    /// Payload for Incoming Payment re-sync (required when document_type = "incoming_payment").
    /// </summary>
    public SapIncomingPaymentRequest? IncomingPayment { get; set; }

    /// <summary>
    /// Payload for AR Credit Memo re-sync (required when document_type = "credit_memo").
    /// </summary>
    public SapCreditMemoRequest? CreditMemo { get; set; }

    /// <summary>
    /// Payload for Goods Return re-sync (required when document_type = "goods_return").
    /// </summary>
    public SapGoodsReturnRequest? GoodsReturn { get; set; }
}
