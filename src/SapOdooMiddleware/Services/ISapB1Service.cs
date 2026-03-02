using SapOdooMiddleware.Models.Sap;

namespace SapOdooMiddleware.Services;

/// <summary>
/// Abstraction over the SAP B1 DI API for sales-order and pick-list operations.
/// </summary>
public interface ISapB1Service
{
    /// <summary>
    /// Creates a Sales Order in SAP B1 via DI API and optionally a Pick List.
    /// </summary>
    Task<SapSalesOrderResponse> CreateSalesOrderAsync(SapSalesOrderRequest request);

    /// <summary>
    /// Updates an existing Sales Order in SAP B1 via DI API by DocEntry,
    /// refreshing UDFs including <c>U_Odoo_LastSync</c> and <c>U_Odoo_SyncDir</c>.
    /// </summary>
    Task<SapSalesOrderResponse> UpdateSalesOrderAsync(int docEntry, SapSalesOrderRequest request);

    /// <summary>
    /// Creates an AR Invoice in SAP B1 via DI API, optionally by copying from
    /// a Delivery Note (ODLN) to maintain the SO → Delivery → Invoice chain.
    /// </summary>
    Task<SapInvoiceResponse> CreateInvoiceAsync(SapInvoiceRequest request);

    /// <summary>
    /// Creates an Incoming Payment (ORCT) in SAP B1 via DI API.
    /// Supports cash and bank payments, full and partial allocations across one or more AR Invoices,
    /// and multi-currency handling via a Forex transfer account when required.
    /// </summary>
    Task<SapIncomingPaymentResponse> CreateIncomingPaymentAsync(SapIncomingPaymentRequest request);

    /// <summary>
    /// Updates UDF fields on an existing AR Invoice (OINV) in SAP B1 by DocEntry.
    /// Used to re-sync Odoo traceability fields that were missed during the initial creation.
    /// </summary>
    Task<SapInvoiceResponse> UpdateInvoiceAsync(int docEntry, SapInvoiceRequest request);

    /// <summary>
    /// Updates UDF fields on an existing Incoming Payment (ORCT) in SAP B1 by DocEntry.
    /// Used to re-sync Odoo traceability fields that were missed during the initial creation.
    /// </summary>
    Task<SapIncomingPaymentResponse> UpdateIncomingPaymentAsync(int docEntry, SapIncomingPaymentRequest request);

    /// <summary>
    /// Returns the document status (open/closed) of an AR Invoice (OINV) in SAP B1.
    /// Used to validate that a credit memo can be created against the invoice.
    /// </summary>
    Task<SapInvoiceStatusResponse> GetInvoiceStatusAsync(int docEntry);

    /// <summary>
    /// Creates an AR Credit Memo (ORIN) in SAP B1 via DI API using Copy-To from
    /// the original AR Invoice (OINV).  Every line must carry BaseInvoiceDocEntry
    /// and BaseInvoiceLineNum.  The service validates that the base invoice is open
    /// before attempting creation.
    /// </summary>
    Task<SapCreditMemoResponse> CreateCreditMemoAsync(SapCreditMemoRequest request);

    /// <summary>
    /// Updates UDF fields on an existing AR Credit Memo (ORIN) in SAP B1 by DocEntry.
    /// </summary>
    Task<SapCreditMemoResponse> UpdateCreditMemoAsync(int docEntry, SapCreditMemoRequest request);

    /// <summary>
    /// Returns the document status (open/closed) of a Delivery Note (ODLN) in SAP B1.
    /// Used to validate that a goods return can be created against the delivery.
    /// </summary>
    Task<SapDeliveryStatusResponse> GetDeliveryStatusAsync(int docEntry);

    /// <summary>
    /// Creates a Goods Return (ORDN) in SAP B1 via DI API using Copy-To from
    /// the original Delivery Note (ODLN).  Every line must carry BaseDeliveryDocEntry
    /// and BaseDeliveryLineNum.  When <c>SapBaseInvoiceDocEntry</c> is provided,
    /// the service validates that the related AR Invoice is open before creation.
    /// </summary>
    Task<SapGoodsReturnResponse> CreateGoodsReturnAsync(SapGoodsReturnRequest request);

    /// <summary>
    /// Updates UDF fields on an existing Goods Return (ORDN) in SAP B1 by DocEntry.
    /// </summary>
    Task<SapGoodsReturnResponse> UpdateGoodsReturnAsync(int docEntry, SapGoodsReturnRequest request);

    /// <summary>
    /// Verifies connectivity to the SAP B1 DI API and returns non-secret connection details.
    /// </summary>
    Task<SapB1PingResponse> PingAsync();
}
