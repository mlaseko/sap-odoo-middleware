using SapOdooMiddleware.Models.Inventory;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services.Autohub;

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
    /// VAT groups O1/I1, net (excl-VAT) TZS prices on price lists 1/2/3/4, and the
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

    /// <summary>
    /// Executes the Movement Clock stock-classification query against SAP B1 via DI API
    /// Recordset.DoQuery(). Classifies every active item by sales velocity, recency, and
    /// age into categories (OBSOLETE, DEAD, YEARLY, QUARTERLY, MONTHLY, NEW variants, etc.)
    /// with recommended actions, holding cost estimates, and priority scores.
    /// </summary>
    Task<List<MovementClockItem>> GetMovementClockAsync();

    /// <summary>
    /// Returns the full inventory transaction history for a single item from SAP B1.
    /// Combines OINM-based inventory movements (invoices, deliveries, returns, goods
    /// receipts/issues, inventory transfers, opening balances) with open Purchase Orders.
    /// Optionally filtered by date range.
    /// </summary>
    Task<List<TransactionHistoryItem>> GetTransactionHistoryAsync(
        string itemCode, DateOnly? fromDate, DateOnly? toDate, CancellationToken ct);

    /// <summary>
    /// Sets a single price-list price on an existing OITM item in SAP B1.
    /// <paramref name="priceListIndex"/> is the 0-based index into the DI API PriceList
    /// collection (0 = PL01, 1 = PL02, 2 = PL03, 3 = PL04).
    /// Used by the PL04 backfill to stamp the Maasai price on existing items.
    /// </summary>
    Task SetPriceListPriceAsync(string itemCode, int priceListIndex, decimal netPrice, CancellationToken ct);

    /// <summary>
    /// Returns ItemCode → ItmsGrpCod for every active item in SAP B1 (OITM).
    /// Uses a single Recordset query — no per-item overhead. Used by the PL04 backfill
    /// to resolve pricing categories from the authoritative SAP group code rather than
    /// relying on Neon's potentially-incomplete copy.
    /// </summary>
    Task<Dictionary<string, int>> GetItemGroupCodesAsync(CancellationToken ct);

    // ================================
    // INVENTORY APP (Autohub) — resolved via IAutohubSapB1Service
    // ================================

    /// <summary>
    /// Creates an Inventory Transfer Request (OWTQ, object 1250000001) — intent only,
    /// no stock movement, no bin allocations. <paramref name="series"/> is set explicitly
    /// on the document; the request's AppRef GUID is written to the U_AppRef UDF for
    /// idempotency. Returns the new DocEntry/DocNum.
    /// </summary>
    Task<InventoryDocResult> CreateInventoryTransferRequestAsync(
        TransferRequestCreate request, int series, CancellationToken ct);

    /// <summary>
    /// Updates a still-open Inventory Transfer Request: absolute quantity changes on
    /// open lines, appended lines (inheriting the header route), and replaced
    /// comments. The plan has already been validated against a fresh SQL snapshot;
    /// SAP re-validates on Update and remains the final authority.
    /// </summary>
    Task UpdateInventoryTransferRequestAsync(
        int docEntry, TransferRequestUpdatePlan plan, CancellationToken ct);

    /// <summary>
    /// Closes an open Inventory Transfer Request, cancelling every remaining open
    /// quantity. Already-fulfilled quantities are untouched.
    /// </summary>
    Task CloseInventoryTransferRequestAsync(int docEntry, CancellationToken ct);

    /// <summary>
    /// Creates an Inventory Transfer (OWTR, object 67). Lines drawn from a transfer
    /// request carry BaseType 1250000001 + BaseEntry/BaseLine so SAP decrements and
    /// closes the request's open quantities (partial fulfillment supported). Bin
    /// allocations are added per line for whichever side(s) are bin-managed
    /// (batFromWarehouse for the source bin, batToWarehouse for the destination bin).
    /// Same-warehouse putaway (FromWhs == ToWhs with both allocation rows) is supported.
    /// </summary>
    Task<InventoryDocResult> CreateInventoryTransferAsync(
        TransferCreate request, int series, CancellationToken ct);

    /// <summary>
    /// Creates an Inventory Counting session (OINC, object 1470000065) via
    /// <c>InventoryCountingsService</c> with one line per seed (item + warehouse +
    /// optional BinEntry). SAP snapshots the system quantity per line at creation.
    /// Counted quantities are left empty for later capture.
    /// <paramref name="bplId"/> sets the header branch (OINC.BPLId) — required when
    /// SAP's multi-branch feature is enabled.
    /// </summary>
    Task<InventoryDocResult> CreateInventoryCountingAsync(
        DateTime countDate, string appRef, List<CountingLineSeed> lines, int series,
        int? bplId, CancellationToken ct);

    /// <summary>
    /// Updates counted quantities on an existing Inventory Counting via
    /// <c>InventoryCountingsService.Update</c>: sets CountedQuantity + Counted=Y on the
    /// lines in <paramref name="updates"/>, and appends <paramref name="additions"/> as
    /// new counted lines (unexpected finds) in <paramref name="additionsWhsCode"/>.
    /// </summary>
    Task UpdateInventoryCountingLinesAsync(
        int docEntry, List<CountingLineUpdate> updates, List<CountingLineAddition> additions,
        string additionsWhsCode, CancellationToken ct);

    /// <summary>
    /// Creates an Inventory Posting (OIQR, object 10000071) from reviewer-approved
    /// counting lines via <c>InventoryPostingsService</c>, with base refs
    /// (BaseEntry = counting DocEntry, BaseLine = counting LineNum) so SAP posts the
    /// stock/GL adjustments and closes those counting lines.
    /// <paramref name="bplId"/> sets the header branch (OIQR.BPLId) — required when
    /// SAP's multi-branch feature is enabled.
    /// </summary>
    Task<InventoryDocResult> CreateInventoryPostingAsync(
        int countingDocEntry, List<CountingPostLine> lines, string appRef, int series,
        int? bplId, CancellationToken ct);

    /// <summary>
    /// Creates a STANDALONE Inventory Posting (OIQR) with no base counting document —
    /// a direct stock correction. Each line carries item + warehouse (+ bin) +
    /// CountedQuantity as the absolute new quantity; SAP computes the variance against
    /// current stock at post time and writes the stock/GL adjustment.
    /// </summary>
    Task<InventoryDocResult> CreateDirectInventoryPostingAsync(
        DirectPostingCreate request, int series, int? bplId, CancellationToken ct);

    /// <summary>
    /// Creates a Goods Receipt (OIGN, object 59, <c>oInventoryGenEntry</c>) — standalone
    /// non-PO stock-in. UnitPrice is set per line only when a unit cost is supplied
    /// (otherwise SAP uses the item cost). Lines carry a single destination bin
    /// allocation when a bin is set.
    /// </summary>
    Task<InventoryDocResult> CreateGoodsReceiptAsync(
        GoodsReceiptCreate request, int series, CancellationToken ct);

    /// <summary>
    /// Sets the item's default bin in one warehouse (OITW.DftBinAbs via
    /// <c>Items.WhsInfo.DefaultBin</c>). Used by the app's "save as default bin for
    /// this item" action so the destination resolver auto-selects it next time.
    /// No-op when the default is already this bin.
    /// </summary>
    Task SetItemDefaultBinAsync(string itemCode, string whsCode, int binAbs, CancellationToken ct);

    /// <summary>
    /// Creates a Goods Receipt PO (OPDN, object 20, <c>oPurchaseDeliveryNotes</c>)
    /// drawn from open purchase order lines: every line carries BaseType 22 +
    /// BaseEntry/BaseLine so SAP updates the PO's open quantities and vendor
    /// liability, closing fully received lines. Partial receipts supported.
    /// </summary>
    Task<InventoryDocResult> CreateGrpoAsync(GrpoCreate request, int series, CancellationToken ct);

    /// <summary>
    /// Sets the item's default bin across multiple warehouses in ONE
    /// <c>Items.Update()</c> (the spec §10 seeding-job shape — one update per item,
    /// not per warehouse). Warehouses whose default already equals the target are
    /// left untouched. Returns true when an Update was actually performed.
    /// </summary>
    Task<bool> SetItemDefaultBinsAsync(
        string itemCode, IReadOnlyDictionary<string, int> binByWhs, CancellationToken ct);

    /// <summary>
    /// Creates a sales Return Request (ORRR) for the Autohub inventory app: every line
    /// copies from an AR invoice line (BaseType 13 + BaseEntry/BaseLine) so prices and
    /// the document chain stay intact. No stock moves. <paramref name="bplId"/> stamps
    /// the header branch when set.
    /// </summary>
    Task<InventoryDocResult> CreateAutohubReturnRequestAsync(
        ReturnRequestCreate request, int series, int? bplId, CancellationToken ct);

    /// <summary>
    /// Creates a Goods Return (ORDN, object 16) for the Autohub inventory app by
    /// copying from open Return Request lines (BaseType = ORRR) — SAP closes the
    /// request's open quantities and stock comes back in. Lines carry a destination
    /// bin allocation when a bin is set. <paramref name="bplId"/> stamps the header
    /// branch when set.
    /// </summary>
    Task<InventoryDocResult> CreateAutohubGoodsReturnAsync(
        GoodsReturnCreate request, int series, int? bplId, CancellationToken ct);

    /// <summary>
    /// Cancels an Incoming Payment (ORCT) via <c>Payments.Cancel()</c>. Pre-checks
    /// ORCT.Canceled — an already-cancelled payment returns success idempotently with
    /// <c>AlreadyCancelled = true</c>. SAP's rejection messages (e.g. deposited or
    /// reconciled payments) are passed through verbatim.
    /// <para>Note: payment cancellation flips the payment to Cancelled and posts a
    /// reversing journal entry — it does NOT create a new payment document. Any new
    /// ORCT row appearing after a cancel came from a separate creation call.</para>
    /// </summary>
    Task<SapPaymentCancelResponse> CancelIncomingPaymentAsync(int docEntry);

    /// <summary>
    /// Finds the Incoming Payments applied to an AR Invoice via the RCT2 allocation
    /// lines (InvType 13). Returns every matching payment (DocEntry, DocNum, whether
    /// it is already cancelled), newest first.
    /// </summary>
    Task<List<(int DocEntry, int DocNum, bool Cancelled)>> FindIncomingPaymentsByInvoiceAsync(
        int invoiceDocEntry);

    /// <summary>
    /// Cancels a Return Request (ORRR) via <c>Documents.Cancel()</c>. Already-cancelled
    /// documents return success idempotently with <c>AlreadyCancelled = true</c>.
    /// SAP creates the cancellation document itself; a closed request (already fully
    /// drawn to a Goods Return) is rejected by SAP with its own message.
    /// </summary>
    Task<DocCancelResult> CancelAutohubReturnRequestAsync(int docEntry, CancellationToken ct);

    /// <summary>
    /// Re-bins RELEASED pick list lines (OPKL/PKL1/PKL2) via
    /// <c>PickLists.UpdateReleasedAllocation</c>: each line's bin allocation rows are
    /// replaced by the plan's set (existing rows zeroed, requested rows set/added).
    /// Quantities and statuses are untouched. When <paramref name="note"/> is set it is
    /// the complete replacement OPKL.Remarks value (existing remarks + appended audit
    /// note, pre-truncated by the caller) written afterwards; a note failure is
    /// reported (false), never thrown, because the allocation change has already
    /// committed.
    /// </summary>
    Task<bool> UpdatePickListAllocationsAsync(
        int absEntry, List<PickListLineWrite> lines, string? note, CancellationToken ct);

    /// <summary>
    /// Picks pick-list lines via <c>PickLists.Update</c>: sets the ABSOLUTE
    /// PickedQuantity per planned line (below the releasable total leaves the line
    /// Partially Picked) and replaces the line's bin allocation rows with the plan's
    /// breakdown. When <paramref name="note"/> is set, it is the complete replacement
    /// OPKL.Remarks value written in the same Update. Returns whether the note was
    /// written.
    /// </summary>
    Task<bool> PickPickListLinesAsync(
        int absEntry, List<PickListLineWrite> lines, string? note, CancellationToken ct);
}
