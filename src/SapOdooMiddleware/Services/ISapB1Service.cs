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
    /// Reads a Delivery Note and returns all unique Odoo SO refs
    /// from the base documents. Used for multi-SO delivery handling.
    /// </summary>
    Task<List<string>> ReadDeliveryBaseSoRefsAsync(int docEntry);

    /// <summary>
    /// Returns the document status (open/closed) of a Return Request (ORRR) in SAP B1.
    /// Odoo gates return validation on this — the picking can only be validated
    /// once the Return Request is closed (SAP has processed the inventory adjustment).
    /// </summary>
    Task<SapReturnRequestStatusResponse> GetReturnRequestStatusAsync(int docEntry);

    /// <summary>
    /// Creates a Return Request (ORRR) in SAP B1 via DI API using Copy-To from
    /// the A/R Invoice (OINV).  <c>SapBaseInvoiceDocEntry</c> is required — the
    /// service validates that the invoice is open and resolves line numbers by
    /// matching ItemCode.
    /// </summary>
    Task<SapGoodsReturnResponse> CreateGoodsReturnAsync(SapGoodsReturnRequest request);

    /// <summary>
    /// Updates UDF fields on an existing Return Request (ORRR) in SAP B1 by DocEntry.
    /// </summary>
    Task<SapGoodsReturnResponse> UpdateGoodsReturnAsync(int docEntry, SapGoodsReturnRequest request);

    /// <summary>
    /// Cancels a Goods Return (ORDN) in SAP B1 by DocEntry.
    /// </summary>
    Task CancelGoodsReturnAsync(int docEntry);

    /// <summary>
    /// Creates a Customer (BusinessPartner CardType=C) in SAP B1 via DI API.
    /// Returns the auto-generated CardCode for write-back to Odoo.
    /// </summary>
    Task<SapCustomerResponse> CreateCustomerAsync(SapCustomerRequest request);

    /// <summary>
    /// Updates an existing Customer (BusinessPartner) in SAP B1 via DI API by CardCode.
    /// Only non-null fields in the request are applied.
    /// </summary>
    Task<SapCustomerResponse> UpdateCustomerAsync(string cardCode, SapCustomerRequest request);

    /// <summary>
    /// Creates a Sales Employee in SAP B1 OSLP table via DI API.
    /// Returns the auto-generated SlpCode for write-back to Odoo.
    /// </summary>
    Task<SapSalesEmployeeResponse> CreateSalesEmployeeAsync(SapSalesEmployeeRequest request);

    /// <summary>
    /// Updates an existing Sales Employee in SAP B1 OSLP table by SlpCode.
    /// </summary>
    Task<SapSalesEmployeeResponse> UpdateSalesEmployeeAsync(int slpCode, SapSalesEmployeeRequest request);

    /// <summary>
    /// Lists all Sales Employees from SAP B1 OSLP table.
    /// Used for one-time sync between Odoo and SAP.
    /// </summary>
    Task<List<SapSalesEmployeeResponse>> ListSalesEmployeesAsync();

    /// <summary>
    /// Creates required User-Defined Fields (UDFs) in SAP B1 if they don't already exist.
    /// Returns a list of UDFs that were created or already existed.
    /// </summary>
    Task<List<string>> EnsureUdfsAsync();

    /// <summary>
    /// Verifies connectivity to the SAP B1 DI API and returns non-secret connection details.
    /// </summary>
    Task<SapB1PingResponse> PingAsync();

    /// <summary>
    /// Looks up a SAP document by its Odoo reference stored in a UDF.
    /// Used by the SAP Field Sync page to find missing SAP identifiers.
    /// Returns null if no matching document is found.
    /// </summary>
    /// <param name="documentType">
    /// One of: sales-order, delivery, invoice, payment, return, credit-memo.
    /// </param>
    /// <param name="odooRef">
    /// The Odoo document name (e.g. "SO0042", "WH/OUT/000106", "INV/2026/00001").
    /// </param>
    Task<SapDocumentLookupResponse?> LookupDocumentAsync(string documentType, string odooRef);

    /// <summary>
    /// Reads an existing AR Invoice (OINV) from SAP B1 by DocEntry and returns
    /// the header identifiers plus line-level cost data (GrossBuyPrice) needed
    /// for COGS journal creation.  Does NOT modify the document.
    /// </summary>
    Task<SapInvoiceResponse> ReadInvoiceCostsAsync(int docEntry);
}
