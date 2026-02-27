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
    /// Verifies connectivity to the SAP B1 DI API and returns non-secret connection details.
    /// </summary>
    Task<SapB1PingResponse> PingAsync();
}
