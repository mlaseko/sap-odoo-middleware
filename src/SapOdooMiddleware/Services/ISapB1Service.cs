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
    /// Reads the delivered items from a SAP delivery note.
    /// Returns item codes and quantities for partial delivery handling.
    /// </summary>
    Task<List<(string ItemCode, double Quantity)>> ReadDeliveryLinesAsync(int docEntry);

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
    /// Cancels a Credit Memo (ORIN) in SAP B1 by DocEntry.
    /// </summary>
    Task CancelCreditMemoAsync(int docEntry);

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

    // ================================
    // ITEM PROVISIONING (Lubes)
    // ================================

    /// <summary>
    /// Returns true if an OITM item with the given ItemCode already exists in SAP B1.
    /// Used by Item Provisioning for an idempotency pre-check.
    /// </summary>
    Task<bool> ItemExistsAsync(string itemCode);

    /// <summary>
    /// Creates a Liqui Moly item master (OITM) in SAP B1 via DI API:
    /// item type I, Inventory/Sales/Purchase = Y, UoM group "Packing Units",
    /// VAT groups O1/I1, net (excl-VAT) TZS prices on price lists 1/2/3, and the
    /// <c>U_Odoo_Category</c> UDF set to the Odoo category name. <c>U_Odoo_Product_ID</c>
    /// is left empty at create and stamped later by the backref worker.
    /// </summary>
    Task CreateLubesItemAsync(SapLubesItemRequest request);

    /// <summary>
    /// Creates a spare-parts item master (OITM) in SAP B1 (Molas Autohub) via DI API:
    /// item type I, Inventory/Sales/Purchase = Y, UoM group "Packing Units", VAT groups O1/I1,
    /// TZS prices on price lists 1/3/5 (Cost/Retail/Wholesale), and the U_Article_No / U_Description /
    /// U_FitForAuto / U_ImageUrl UDFs. OEM cross-references are NOT written to SAP (kept in Neon).
    /// </summary>
    Task CreateAutohubItemAsync(SapAutohubItemRequest request);

    /// <summary>
    /// Stamps the Odoo product id onto the SAP item's <c>U_Odoo_Product_ID</c> UDF.
    /// Used by the backref worker once the Neon → Odoo automation has created the product.
    /// </summary>
    Task UpdateOdooProductIdAsync(string itemCode, string odooProductId);

    /// <summary>
    /// Returns a snapshot of the existing OITM item's Odoo-category UDF and price-list
    /// prices, or <c>null</c> if the item does not exist. Used by the orchestrator to
    /// decide between create and idempotent recovery.
    /// </summary>
    Task<SapItemSnapshot?> GetItemSnapshotAsync(string itemCode, CancellationToken ct);

    /// <summary>
    /// Idempotent recovery for an item that already exists in SAP: fills only the
    /// blank fields (empty <c>U_Odoo_Category</c> UDF and/or any price-list price that
    /// is 0) from <paramref name="desired"/>. Never overwrites a non-blank SAP value,
    /// and only calls <c>Items.Update()</c> when at least one blank field needs filling.
    /// </summary>
    Task UpdateBlankFieldsAsync(string itemCode, SapLubesItemRequest desired, CancellationToken ct);

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

    /// <summary>
    /// Finds Delivery Notes (ODLN) created from a given Sales Order DocEntry.
    /// Traces the DLN1 → BaseEntry relationship where BaseType = 17 (Sales Order).
    /// Returns the first (most recent) delivery's DocEntry/DocNum/Status,
    /// or null if no delivery exists for the given SO.
    /// </summary>
    Task<SapDeliveryStatusResponse?> FindDeliveryByOrderAsync(int soDocEntry);
    /// Executes the inventory valuation SQL against SAP B1 via DI API Recordset.DoQuery()
    /// and returns the total on-hand inventory value in TZS as of <paramref name="asOfDate"/>.
    /// When <paramref name="asOfDate"/> is null, today's server date is used.
    /// </summary>
    Task<decimal> GetInventoryValuationTotalAsync(DateOnly? asOfDate);

    /// <summary>Creates a Purchase Order in SAP B1 (oPurchaseOrders) and returns its DocEntry/DocNum.</summary>
    Task<SapPurchaseOrderResponse> CreatePurchaseOrderAsync(SapPurchaseOrderRequest request);

    /// <summary>
    /// Returns the DocEntry/DocNum of an existing open/closed Purchase Order for the given vendor and
    /// vendor reference (OPOR.CardCode + OPOR.NumAtCard), or null if none — used to prevent duplicate POs.
    /// </summary>
    Task<(int DocEntry, int DocNum)?> FindPurchaseOrderByNumAtCardAsync(string cardCode, string numAtCard);
}
