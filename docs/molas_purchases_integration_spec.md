# Spec: SAP B1 ↔ Odoo Purchases, Supplier Payments & Landed Costs — Molas Lubes

**Version 4 — 2026-10-20.** This version supersedes v1, v2 and v3.

Main changes from v3 (decisions taken 2026-10-20):
- **Odoo → SAP goes through the Integration Control Centre job pattern**, not a middleware poller. The control centre creates an `integration.job` after the Odoo posting; the job cron POSTs to idempotent, authenticated middleware endpoints and writes the SAP keys back. The Odoo posting never waits on SAP and is never rolled back by it.
- **Vendor bills reuse the existing `account.move` fields** `x_sap_invoice_docentry` and `x_sap_credit_docentry`. The existing `x_sap_docentry` on `account.move` stays the SAP *sales order* DocEntry and is never written by this flow.
- **Mapping tables live in the control centre (Odoo), not in Neon.** Accounts edits them in the Odoo UI; the middleware reads them over JSON-RPC. The existing `sap.journal.mapping` is the journal → SAP bank account map.
- **The SAP → Odoo queue is the existing `ODOO_WEBHOOK_QUEUE` table**, unchanged in schema, with `EventType` carrying the purchase actions. There is no `OdooSyncQueue`.
- DI API is **SAP Business One 10.0 (10.00.280) FP 2502, 64-bit**. Landed Costs use `LandedCostsService` directly, no fallback.
- Cut-over rules are spelled out (section 5.5 and 6.6).
- Phase 3 is split into two PRs (middleware endpoints, then the Odoo side).

**Audience:** two agents build this, and both get this same file.
- The **middleware agent** builds the SAP side and section 6, in `mlaseko/sap-odoo-middleware`.
- The **Odoo agent** builds section 5, in `mlaseko/Molas_Lubes`: the Shipments app (`custom/addons/molas_supplier_orders`) **and** the Integration Control Centre (`custom/addons/integration_control_center`).

The field and model names in sections 5.2, 5.7 and 6.2, the queue `EventType` values in 4.2, and the states used throughout are the contract between them. Neither agent renames them without updating this spec.

---

## 1. Context

| System | Source of truth for |
|---|---|
| **SAP B1** | Inventory and item cost; goods purchasing documents (PO, GRPO, A/P Invoice, **A/P Credit Memo — including rebates and bonuses**); Landed Costs documents (created by the middleware) |
| **Odoo 19** (Odoo.sh) | General ledger; customer invoices; expenses; **all payments** |
| **Shipments app** (`molas_supplier_orders`) | Shipment and container orders (`supplier.order`, one order = one container), landed-cost lines, TANSAD records, and **Account Activity**: supplier current accounts (`supplier.account`, `supplier.account.entry`, `supplier.allocation`) with FIFO allocation of payments to invoiced orders |
| **Integration Control Centre** (`integration_control_center`) | The Odoo → middleware job queue (`integration.job`), automated actions, HTTP logs, retries, the middleware backend URL and API key, the SAP mapping tables, and the monitoring dashboards |
| **Middleware** (`sap-odoo-middleware`, .NET 8, Windows service, `https://molas-api.app`) | SAP reads and writes (DI API), the SAP → Odoo queue worker, the Odoo JSON-RPC client |

- **Tenant:** `Lubes` only (SAP company `Molas_Lubes_LTD`; the middleware's default tenant).
- **Reuse:**
  - `SBO_SP_TransactionNotification` → `ODOO_WEBHOOK_QUEUE` → middleware background worker (today used for delivery confirmations)
  - `OdooJsonRpcService` (Bearer API key, `/json/2/` API)
  - the middleware's existing SAP write path: **DI API only** (`SapB1DiApiService`). Service Layer is not used for any document type.
  - the control centre's `integration.automated.action` → `integration.job` → job cron → middleware endpoint → write-back pattern, exactly as customer payments use today (`POST /api/incoming-payments`)
  - `sap.journal.mapping` (Odoo journal + currency → SAP bank G/L, with the forex transfer account)
  - `res.partner.x_sap_cardcode` (already exists)

## 2. Design principles (do not deviate)

1. **No stock documents in Odoo.** No `purchase.order`, `stock.picking` or stock moves for this flow, and no use of the Odoo Purchase app.
2. **Goods documents originate in SAP:**
   - SAP A/P Invoice → Odoo Vendor Bill
   - SAP A/P Credit Memo → Odoo Vendor Credit Note

   **Credit notes are never typed in the Shipments app.** This includes rebates and bonuses with no goods attached, which are entered in SAP as service-type credit memos. Bill lines never use storable products, and every line has an explicit account.
3. **Supplier current accounts.**
   - Payments to goods suppliers such as Liqui Moly are **top-ups to a running account** on a credit line.
   - They are entered in Account Activity, and each money entry creates and **auto-posts** exactly one Odoo `account.payment`.
   - The control centre pushes each posted payment to SAP **on account**: never applied to an invoice, and with **no SAP internal reconciliation, ever**.
   - The SAP supplier balance is correct; SAP's invoice-level aging is not used.
4. **Allocation lives in the app and is mirrored only in Odoo.**
   - The app's FIFO and manual allocations (entry → order → amount) are mirrored as **Odoo partial reconciliations** between the payment or credit note and the order's goods bill.
   - Once mirrored, an allocation is **locked**. FIFO treats locked lines like manual ones and only fills the remaining capacity.
5. **Import costs** are billed **in Odoo only**, from the Shipments app. That covers the clearing agent, TRA (TANSAD), shipping line, wharfage, TBS, GCLA, insurance and transport.
   - The cost vendors never exist in SAP.
   - Only the final capitalised amounts per container go to SAP, as a **Landed Costs document**.
6. **Automatic posting** (accounts does not post manually):
   - Payments post when they are recorded.
   - Shipments cost bills and TANSAD bills post when they are generated.
   - SAP-synced goods bills and credit notes post when their totals match and every mapping resolves. A **total mismatch** leaves the document in draft with `x_sap_sync_error = TOTAL_MISMATCH`. A **missing mapping** (partner, account, tax) creates nothing: the queue row fails with the code, is visible in the control centre, and is retried after the mapping is added.
7. **Odoo → SAP is asynchronous and never blocks Odoo.** A posting in Odoo succeeds whether or not SAP or the middleware is reachable. The job retries; failures are visible in the control centre with a retry button.
8. **Every middleware write endpoint is idempotent.** Before creating anything it checks the SAP idempotency UDF: `U_OdooPayRef` on `OVPM` / `ORCT`, `U_OdooRef` on `OJDT`, `U_OdooShipmentId` on `OIPF`. A retried job never duplicates a SAP document; it gets the existing document back.
9. **Every middleware endpoint is authenticated** with the `X-Api-Key` header, as today. No open endpoints on the public URL.
10. **Idempotent in both directions.** Every synced record stores the other system's key, with unique constraints.
11. **The middleware creates the SAP UDFs** via an idempotent setup routine (4.1).

## 3. Document flows

### Flow A — Goods invoice and credit note (SAP → Odoo)

```
SAP:  PO ──→ GRPO (one per container, U_ShipRef = container no.) ──→ A/P Invoice / A/P Credit Memo
      SBO_SP_TransactionNotification → ODOO_WEBHOOK_QUEUE (EventType = PO_UPSERT / GRPO_LINK / AP_INVOICE_CREATE / AP_CREDIT_CREATE)
Middleware worker: reads SAP by SQL, resolves the control-centre mappings, writes Odoo by JSON-RPC
Odoo: x.sap.po     x.sap.grpo (linked to supplier.order by container_no)   Vendor Bill / Credit Note (auto-posted)
                                                                            └─ linked to order(s) via NumAtCard match
```

### Flow B — Supplier payment (top-up) and allocation (Odoo → SAP)

```
App:  Account Activity → Payment entry (EUR, day rate, SWIFT ref)
Odoo: account.payment created + POSTED → Dr Supplier payable / Cr Bank          (x_sap_sync_state = pending)
ICC:  automated action → integration.job → POST /api/outgoing-payments
SAP:  Outgoing Payment ON ACCOUNT to the supplier BP (U_OdooPayRef = payment name) — nothing else in SAP
ICC:  write-back x_sap_docentry, x_sap_objtype = 46, x_sap_sync_state = synced
App:  FIFO / manual allocation entry → order → amount
Odoo: for each allocation whose order has a posted goods bill:
        partial reconciliation payment ↔ bill for exactly that amount (EUR) → allocation LOCKED
        realised FX gain/loss posted automatically by Odoo
      allocations whose bill hasn't synced yet stay "pending_sync"
```

Synced vendor credit notes take part in FIFO in the same way as payments: they show in Account Activity as read-only Credit Note entries, and their allocations reconcile credit note ↔ bill.

### Flow C — Refund from the supplier (Odoo → SAP)

```
App:  Refund entry → Odoo inbound account.payment from supplier, posted (pending)
ICC:  job → POST /api/supplier-refunds
SAP:  INCOMING Payment from the vendor BP, on account (U_OdooPayRef)
ICC:  write-back x_sap_docentry, x_sap_objtype = 24, synced
```

### Flow D — Adjustment (two reasons only) (Odoo → SAP)

```
App:  Adjustment entry with reason ∈ {bank_charges, small_balance}
Odoo: JE  bank_charges : Dr Bank Charges        / Cr Supplier payable
          small_balance: Dr/Cr Small Balance Adj / Cr/Dr Supplier payable   (|amount| ≤ limit)
ICC:  job → POST /api/supplier-adjustments
SAP:  Journal Entry with BP line on the supplier CardCode + the mirror G/L account (U_OdooRef = entry id)
ICC:  write-back x_sap_jdt_docentry, synced
```

### Flow E — Landed costs (Odoo → SAP, Phase 4)

```
App:  container order linked to its SAP GRPO (U_ShipRef)
      cost lines + TANSAD → Odoo vendor bills (auto-posted)
        capitalised types → Odoo clearing accounts (mirror of SAP 20154xx)
        import VAT → Input VAT; demurrage/storage → expense
      "Costs Final" pressed → x_lc_state = costs_final
ICC:  job → POST /api/landed-costs  (amounts per LC code in TZS, GRPO DocEntry, previous LC DocEntry for a delta)
Middleware: SAP Landed Costs document on the container's GRPO, no Vendor/Broker, U_OdooShipmentId, U_ShipRef
ICC:  write-back x_sap_lc_docentries, x_lc_state = pushed,
      then the Odoo JE: Dr Purchases – Landed Costs / Cr each clearing account (x_sap_lc_docentry)
Late cost after push: order returns to costs_final → delta Landed Costs document based on the previous one + delta JE
```

### Cancellations

- **SAP A/P invoice or credit memo cancelled** (`AP_INVOICE_CANCEL` / `AP_CREDIT_CANCEL`):
  - if the Odoo document is in draft, cancel it;
  - if it is posted and unreconciled, create a reversal;
  - if it is posted and reconciled, log `CANCEL_AFTER_ALLOCATION` and do nothing automatic.
- **Odoo payment or refund cancelled or reset to draft:** the app unreconciles it in Odoo and releases its locked allocations (FIFO re-runs); the control centre creates a cancel job (`POST /api/outgoing-payments/{doc_entry}/cancel` or `/api/supplier-refunds/{doc_entry}/cancel`); on success `x_sap_sync_state = cancelled`. The SAP keys are **kept** on the Odoo payment (they are the audit trail).
- **Odoo adjustment reversed:** the control centre creates a reverse job (`POST /api/supplier-adjustments/{jdt_docentry}/reverse`).
- If SAP refuses a cancel or reverse: `x_sap_sync_state = error`, job failed, no auto-retry; a person decides.

## 4. SAP B1 changes

### 4.1 UDFs — created by the middleware

Extend the middleware's existing UDF routine (`EnsureUdfsAsync`, `UserFieldsMD` through the DI API) and expose it as `POST /api/admin/setup-sap-udfs` (and the `setup-sap-udfs --tenant Lubes` CLI flag):
- **Check first, create only what is missing,** so it is safe to re-run. The second run reports `EXISTS` for every field.
- **It needs exclusive access** to the Lubes company database. It checks the SQL Server sessions on the company database (excluding its own) and refuses with a clear message naming the connected logins when anyone else is connected. Run it out of hours.
- Log what was created and update the docs.

| Table | Field (SAP adds `U_`) | Type / size | Purpose |
|---|---|---|---|
| `OPDN` (GRPO) | `ShipRef` | Alpha 50 | Container number. Links the GRPO to the container order. Set at goods receipt, including by the inventory app. |
| `OPDN` | `OdooShipmentId` | Alpha 30 | Written by the middleware when linked |
| `OIPF` (Landed Costs) | `OdooShipmentId` | Alpha 30 | Idempotency key |
| `OIPF` | `ShipRef` | Alpha 50 | Readability |
| `OVPM` (Outgoing Payments) | `OdooPayRef` | Alpha 50 | Idempotency key for pushed payments |
| `ORCT` (Incoming Payments) | `OdooPayRef` | Alpha 50 | Idempotency key for refunds |
| `OJDT` (Journal Entries) | `OdooRef` | Alpha 50 | Idempotency key for adjustment JEs |

Follow-up for the inventory app's maintainer: receive each import container as its own GRPO, with `U_ShipRef` set.

**DI API:** SAP Business One 10.0 (10.00.280) FP 2502, **64-bit**. The middleware process that loads it runs as 64-bit (no x86, no "Prefer 32-bit"). Landed Costs use `LandedCostsService` directly, with no fallback.

### 4.2 `SBO_SP_TransactionNotification` and `ODOO_WEBHOOK_QUEUE`

The queue is the **existing** `[dbo].[ODOO_WEBHOOK_QUEUE]` table, with **no schema change**:

```
Id int IDENTITY | ObjectType nvarchar(20) NOT NULL | TransType nvarchar(10) NOT NULL | DocEntry int NOT NULL
DocNum int NULL | OdooSoId nvarchar(50) NULL | OdooDeliveryId nvarchar(50) NULL | BaseSODocEntry int NULL
DeliveryDate datetime NULL | CardCode nvarchar(15) NULL | Status nvarchar(20) NOT NULL default 'pending'
RetryCount int NOT NULL default 0 | MaxRetries int NOT NULL default 5 | ErrorMessage nvarchar(max) NULL
ResponseBody nvarchar(max) NULL | CreatedAt datetime NOT NULL default getdate() | ProcessedAt datetime NULL
EventType nvarchar(50) NOT NULL default 'webhook'
```

| ObjType | Document | Transactions | `EventType` |
|---|---|---|---|
| 22 | Purchase Order | A, U, L, C | `PO_UPSERT` |
| 20 | GRPO | A, U | `GRPO_LINK` |
| 18 | A/P Invoice | A / C | `AP_INVOICE_CREATE` / `AP_INVOICE_CANCEL` |
| 19 | A/P Credit Memo | A / C | `AP_CREDIT_CREATE` / `AP_CREDIT_CANCEL` |

Rules:
- a. New rows fill `ObjectType`, `TransType`, `DocEntry`, `DocNum` and `CardCode`. The delivery-specific columns stay NULL.
- b. `EventType` is always set explicitly on new rows to one of the six values above. Delivery rows keep `'webhook'`; re-sync rows keep `'resync'`.
- c. **The existing delivery worker filters on `ObjectType = '15'`.** This filter ships in the middleware **before** the stored procedure change is deployed. The purchase worker filters on its own six `EventType` values. Neither worker ever sees the other's rows.
- d. Dedupe for the new types: the insert is skipped only if a row exists with the same `ObjectType` + `DocEntry` + `TransType` **and** `Status = 'pending'`.
- e. A non-clustered index on `(Status, EventType, ObjectType, DocEntry)` is added by the same migration.
- f. Each object type gets its own `IF` block with its own `TRY/CATCH`, writing a `'failed'` row exactly like the delivery block. The procedure **never sets `@error`** and never blocks a SAP transaction. The delivery block (object 15) is not modified.
- Objects 69, 46, 24 and 30 are created by the middleware itself and are **not** queued.

The migration lives in the middleware repo under `migrations/sqlserver/` and is applied by hand on the SAP SQL Server, like the existing Neon migrations.

### 4.3 Landed cost codes (manual setup by accounts — prerequisite for Phase 4)

| Code | Name | Allocation by | Account | Status |
|---|---|---|---|---|
| 2 | Import Duty | Cash value before customs | 2015410 | exists |
| 3 | Clearing Agent Fees | Cash value before customs | 2015411 | exists |
| 4 | Customs Processing Fee | Cash value before customs | 2015412 | exists (renamed) |
| 5 | Insurance Charges | Cash value before customs | 2015420 | exists |
| 6 | TBS | Cash value before customs | 2015419 | exists |
| 7 | GCLA Import Permit | Cash value before customs | 2015422 | exists |
| 8 | Wharfage | Cash value before customs | 2015413 | exists |
| new | Railway Development Levy | Cash value before customs | 2015414 | **add** |
| new | Shipping Line Charges | Weight or volume if item weights are maintained, else cash value | 2015415 | **add** |
| new | Transport Charges | Weight or volume, else cash value | 2015416 | **add** |
| new | GCLA Transport Permit | Cash value before customs | 2015421 | **add** |
| new | Other Import Charges | Cash value before customs | 2015423 | **add** |
| new | Excise Duty (import, non-recoverable, so capitalised under IAS 2) | Cash value before customs | new "Clearing – Excise Duty" (e.g. 2015424) | **add** |
| new | Freight (only if any purchases are FOB/EXW or air) | Weight or volume | new "Clearing – Freight" | optional |

- **Demurrage and storage** (2015417 / 2015418) are abnormal costs. They are expensed in Odoo and never sent to SAP.
- **The SAP 20154xx accounts are only ever credited,** because the debits live in Odoo. Their cumulative credits must equal what Odoo has allocated per code. Treat that as a reconciliation check.

### 4.4 FX policy (to be confirmed by the auditor)

- The supplier current account is treated as a **monetary** EUR balance.
- Invoices are booked at SAP `DocRate` (the invoice-date rate), and payments at the day's rate.
- Odoo posts realised FX on reconciliation, and the open EUR payable is revalued at month end using Odoo's unrealised gains/losses adjustment.
- The IFRIC 22 blended-rate rule from v1/v2 is **withdrawn**.

## 5. Odoo changes (Odoo agent)

Implement inside the Shipments module and the Integration Control Centre, or as a sibling module `molas_sap_purchase` that both depend on. Do not use Studio.

### 5.0 Discovery (do first, read-only script for the Odoo.sh shell, report as CSV/markdown)

1. Chart of accounts:
   - existing mirrors of the SAP 20154xx accounts, 2015310 (GRNI), Bank Charges, Small Balance Adj. and the supplier payables;
   - a proposed list of accounts to create. Accounts confirms the codes.
2. Purchase taxes: VAT 18% on services, and how import VAT is set up. Report the options: a tax, or a direct line on Input VAT with a VAT-return tax grid tag. Accounts decides.
3. Journals (bank journals per currency, and the purchase journal the synced bills will use), partners (which Liqui Moly / Meguin partners already carry `x_sap_cardcode`), the `supplier.expense.category` list, and the TANSAD tax heads seen in the data.
4. Confirm the Odoo 19 mechanics:
   - the field for the per-document currency rate on `account.move` (`invoice_currency_rate`) and its direction (Odoo stores foreign-currency units per company-currency unit, i.e. **1 / SAP `DocRate`** when the company currency is TZS and `DocRate` is TZS per EUR). The middleware writes it; discovery confirms the direction with one test bill.
   - how to create a **partial reconciliation for an exact amount** between two move lines in foreign currency (e.g. `account.partial.reconcile` with `amount_currency` fields, versus `reconcile()`). Prove it in a test.
5. Confirm that the `sap-integration` user (5.6) can create and post `account.move` records in the purchase journal and write the `x_*` fields.

### 5.1 Accounts (configured)

- Purchases – Lubricants (plus others if needed)
- Purchases – Landed Costs
- Clearing accounts mirroring each SAP 20154xx in use
- Demurrage & Port Storage (expense)
- **Bank Charges** (finance cost)
- **Small Balance Adj.** (mirror of 6011520)
- GRNI (mirror of 2015310), Inventory and Change in Inventory, for month end
- Unrealised and realised FX gain/loss accounts (confirm the existing ones)

### 5.2 Fields and models

**`res.partner`**
- `x_sap_cardcode` already exists (control centre). Add a unique constraint per company if discovery shows none.

**`account.move`** (existing fields are the control centre's)
- **Vendor bill (`in_invoice`): `x_sap_invoice_docentry`** (existing) holds the SAP **A/P Invoice** (`OPCH`) DocEntry.
- **Vendor credit note (`in_refund`): `x_sap_credit_docentry`** (existing) holds the SAP **A/P Credit Memo** (`ORPC`) DocEntry.
- Uniqueness: one unique index on (`x_sap_invoice_docentry`, `move_type`, `company_id`) and one on (`x_sap_credit_docentry`, `move_type`, `company_id`), both for values > 0.
- **`x_sap_docentry` on `account.move` is the SAP sales order DocEntry on customer invoices. The purchase flow never reads or writes it.**
- New: `x_sap_docnum` (Integer), `x_sap_sync_error` (Char: `TOTAL_MISMATCH`, `CANCEL_AFTER_ALLOCATION`, …)
- New: `x_sap_po_ref_ids` (M2M → `x.sap.po`)
- New: `x_shipment_id` (M2O → `supplier.order`), `x_sap_lc_docentry` (Integer, landed cost JE)
- New: `x_order_ids` (M2M → `supplier.order`: the orders a goods bill covers)

**`account.payment`**
- `x_entry_id` (M2O → `supplier.account.entry`)
- `x_sap_sync_state`: `not_required` / `pending` / `synced` / `cancelled` / `error`. Default `pending` only when the partner has a CardCode and `x_entry_id` is set.
- `x_sap_docentry` (SAP payment DocEntry, unique with `x_sap_objtype`), `x_sap_objtype` (`46` outgoing / `24` incoming), `x_sap_docnum`, `x_sap_sync_error`
- **The existing `write()` override that clears `x_sap_inpay_*` on cancel or reset to draft must not touch these fields.** Vendor payments keep their SAP keys; cancellation creates a cancel job instead (5.7).

**`supplier.account.entry`** (existing, extended)
- `payment_id` (M2O → `account.payment`, for Payment and Refund)
- `move_id` (M2O → `account.move`: the JE for an Adjustment, or the synced credit note for a Credit Note)
- `adjustment_reason`: `bank_charges` / `small_balance` (required for Adjustment)
- `x_sap_jdt_docentry` (SAP JE DocEntry for an Adjustment), `x_sap_sync_state` (same values as the payment), `x_sap_sync_error`
- **Locking:** once posted, amount, currency, rate, date and type are read-only. Corrections are cancel/reverse only.
- **Credit Note entries** are **created only by the sync**, from posted Odoo vendor credit notes (public, idempotent model method `sync_from_credit_note(move_ids)` that the middleware calls after posting the credit note). Users cannot create them.
- **Balance C/F** = the cut-over opening balance only. It creates no payment, and its opening JE is made once by accounts (5.5).

**`supplier.allocation`** (existing, extended)
- `state`: `pending_sync` / `locked` / `error`
- `partial_reconcile_ids` (M2M → `account.partial.reconcile`)
- **FIFO rebuild rule:** a rebuild deletes and recreates only `pending_sync` lines. `locked` and manual lines are kept, and FIFO fills only the remaining capacity.

**`x.sap.po`** (reference only, written by the middleware): `docentry` (unique), `docnum`, `partner_id`, `currency_id`, `doc_total`, `doc_date`, `due_date`, `status`, `num_at_card`.

**`x.sap.grpo`** (reference only, written by the middleware): `docentry` (unique), `docnum`, `po_ids`, `ship_ref`, `doc_date`, `doc_total_lc`, `order_id` (M2O → `supplier.order`), `line_json` (item code and quantity per line, for the qty check).

**`supplier.order`** (the container order, existing, extended): `x_sap_po_ids`, `x_sap_grpo_ids`, `x_sap_ap_invoice_ids`, `x_lc_state` (`draft` / `costs_final` / `pushed` / `error`), `x_sap_lc_docentries`, `x_lc_error`, and a `QTY_MISMATCH` flag. The existing `container_no` and `invoice_no` fields are the match keys (5.3).

**`supplier.expense.category`** (existing, used as the cost type), extended:
- `x_account_id`
- `x_sap_lc_code` (empty if not capitalised)
- `x_capitalised` (Boolean)

**Cost line** (existing, extended):
- `vendor_invoice_date`, separate from `date`
- `move_id`
- `tax_ids` (M2M → `account.tax`), replacing the VAT flag and rate
- `is_paid` becomes **computed, read-only** from `move_id.payment_state`

### 5.3 Linking containers to SAP

- One order = one container. One SAP PO can cover several orders.
- **GRPO ↔ order:** by `U_ShipRef` = `supplier.order.container_no` (`GRPO_LINK`, done by the middleware).
- **Goods bill ↔ orders:** match (supplier CardCode, `NumAtCard` normalised: trimmed, upper-case, no spaces) against `supplier.order.invoice_no` normalised the same way. A single invoice may cover several containers.
- Compare the order's item lines (item code, qty) with the GRPO lines, and flag `QTY_MISMATCH` if they differ.
- When nothing matches, nothing fails: the bill syncs, and the order offers a "Link SAP invoice" action.
- **Goods invoice lines held on orders NEVER generate Odoo bills.** Only cost lines and TANSAD records do.

### 5.4 Account Activity → Odoo (Flows B, C, D)

- **Payment entry:**
  - validations: the supplier account has a partner with a CardCode, a bank journal is set, journal currency = entry currency, amount > 0, rate present;
  - creates and posts an outbound `account.payment` to the partner's payable at the entry's rate;
  - `x_sap_sync_state='pending'`, `x_entry_id` set. The control centre's automated action takes it from there (5.7). **Posting never waits for SAP.**
- **Refund entry:** creates and posts an inbound `account.payment` from the supplier, with `pending`.
- **Adjustment entry:**
  - `bank_charges`: posts a JE Dr Bank Charges / Cr payable.
  - `small_balance`: posts a JE with the payable against Small Balance Adj. `|amount|` must be ≤ the configurable limit (`icc.ap.sync.settings.small_balance_limit`, default €10); above it the entry is **blocked**.
  - The entry's `x_sap_sync_state` becomes `pending`; the control centre pushes it (5.7).
- **Allocation mirroring** (runs after every FIFO rebuild): for each `pending_sync` allocation whose order has a posted goods bill (`x_order_ids`):
  1. Create a partial reconciliation for exactly the allocation amount in EUR, between the payment's (or credit note's) payable line and the bill's payable line. Use the mechanism proven in discovery item 4.
  2. Set the allocation to `locked`. Odoo posts any exchange difference.
  3. If the bill's residual or the payment's residual is too small, set the allocation to `error` with a message. Never force it.
- **Payment cancelled:** unreconcile its partials, set its locked allocations back to `pending_sync`, and re-run FIFO. The control centre's cancel job cancels the SAP payment.
- **Controls:**
  - Only the group "Supplier Account – Payment Poster" can record Payment, Refund and Adjustment entries.
  - Optional maker-checker above a threshold (default off).
  - Cancelling or reversing a posted entry requires the accounting manager role.
- **Pre-opening orders:** skipped by FIFO, as today, and never mirrored.

### 5.5 Shipments → vendor bills (Flow E, Odoo side) and cut-over

- **Cost lines:** one bill per vendor invoice.
  - Partner = the cost vendor; `ref` = the vendor invoice number; date = `vendor_invoice_date`.
  - Each line's account comes from its category's `x_account_id`, with its `tax_ids`.
  - `x_shipment_id` is set. The bill auto-posts.
- **TANSAD:** one bill per TANSAD, with partner "TRA – Customs" and `ref` = the TANSAD number.
  - One line per tax head: duty, CPF, RDL and excise go to their clearing accounts; import VAT goes per discovery item 2.
  - An unmapped head fails with `TAX_HEAD_NOT_MAPPED`.
- Demurrage and storage go to expense.
- **"Costs Final" button:** enabled only when all cost bills are posted and a GRPO is linked. It sets `x_lc_state='costs_final'`; the control centre creates the landed-cost job (5.7, Phase 4).
- Cost-vendor bills are paid by registering a payment on the bill (`not_required` for SAP).
- **Cut-over** (`icc.ap.sync.settings.cutover_date`, **no default**; set at go-live on the first of a month after Phases 1 and 3 pass testing):
  - Before cut-over: nothing is generated, pushed or re-allocated. Older data stays as history: `is_paid` values are frozen into a stored field.
  - At cut-over: the supplier balance enters Odoo once as an opening balance, as the **Balance C/F** entry and its opening JE (made once by accounts).
  - FIFO allocations from post-cut-over payments to pre-cut-over orders reconcile against that opening balance line, not against individual bills.
  - Backfill (6.5) covers only SAP documents dated on or after cut-over.

### 5.6 Integration user

- `sap-integration`, with an API key. The middleware's `Odoo:ApiKey` is switched to it.
- It can create and post SAP-synced bills and credit notes that pass validation, and the landed-cost JE.
- It can write `x_*` fields, `x.sap.po` and `x.sap.grpo`, and read the control centre mapping models (5.7).
- Payments, adjustments and reconciliations are posted by the app under the recording user's rights.

### 5.7 Integration Control Centre

**Mapping and settings models** (menu Integration → Configuration → SAP Purchases; accounts edits them; `sap-integration` reads them):

| Model | Fields | Used by |
|---|---|---|
| `sap.journal.mapping` (**existing**) | `journal_id`, `currency_id`, `sap_account_code`, `sap_forex_account_code`, `use_forex_transfer` | payment and refund jobs: `sap_account_code` becomes the SAP `TransferAccount` |
| `icc.sap.vatgroup.map` | `sap_vat_group` Char (unique), `tax_id` M2O `account.tax`, `active` | middleware, bill and credit-note lines (`PCH1.VatGroup` / `RPC1.VatGroup`) |
| `icc.sap.itemgroup.map` | `sap_item_group` Integer (`OITB.ItmsGrpCod`, unique), `name`, `account_id` M2O `account.account`, `analytic_account_id` M2O (optional) | middleware, item lines of goods bills |
| `icc.sap.glaccount.map` | `sap_gl_code` Char (unique), `account_id` M2O `account.account` | middleware, service-type lines (`AcctCode`) and `PCH3` expense lines |
| `icc.sap.adjustment.map` | `reason` Selection (`bank_charges`, `small_balance`; unique), `account_id` M2O `account.account` (the Odoo side of the JE), `sap_gl_code` Char (the SAP mirror G/L; `small_balance` → `6011520`) | Shipments app (Odoo JE) and the adjustment job payload |
| `icc.ap.sync.settings` (singleton) | `total_tolerance` Float (0.01), `small_balance_limit` Monetary (10 EUR), `cutover_date` Date (**no default**), `bill_journal_id` M2O `account.journal` (purchase journal for synced bills) | middleware and Shipments app |

The middleware reads these with `search_read` and caches them for 5 minutes. A missing key fails the queue row with `PARTNER_NOT_MAPPED`, `ACCOUNT_NOT_MAPPED` or `TAX_NOT_MAPPED` plus the key, and the row is retried from the queue view after the mapping is added.

**Automated actions and jobs** (same pattern as `action_push_payment_to_sap`; all run on the existing 2-minute job cron, after the Odoo posting, never inside it):

| Action | Trigger | Middleware call | Write-back |
|---|---|---|---|
| Push Supplier Payment | `account.payment` on_write: `payment_type=outbound`, `partner_type=supplier`, `state in (paid, in_process)`, `x_entry_id` set, `x_sap_sync_state=pending` | `POST /api/outgoing-payments` | `x_sap_docentry`, `x_sap_docnum`, `x_sap_objtype=46`, `x_sap_sync_state=synced` |
| Push Supplier Refund | same, `payment_type=inbound`, `partner_type=supplier` | `POST /api/supplier-refunds` | same, `x_sap_objtype=24` |
| Cancel Supplier Payment / Refund | `account.payment` on_write: `state in (cancel, draft)`, `x_sap_sync_state=synced` | `POST /api/outgoing-payments/{doc_entry}/cancel` or `POST /api/supplier-refunds/{doc_entry}/cancel` | `x_sap_sync_state=cancelled` |
| Push Supplier Adjustment | `supplier.account.entry` on_write: `entry_type=adjustment`, posted, `x_sap_sync_state=pending` | `POST /api/supplier-adjustments` | `x_sap_jdt_docentry`, `x_sap_sync_state=synced` |
| Reverse Supplier Adjustment | `supplier.account.entry` reversed, `x_sap_sync_state=synced` | `POST /api/supplier-adjustments/{jdt_docentry}/reverse` | `x_sap_sync_state=cancelled` |
| Push Landed Costs (Phase 4) | `supplier.order` on_write: `x_lc_state=costs_final` | `POST /api/landed-costs` | `x_sap_lc_docentries`, `x_lc_state=pushed`, then post the Odoo clearing JE and set `x_sap_lc_docentry` on it; on failure `x_lc_state=error`, `x_lc_error` |

- Any middleware error sets the record's `x_sap_sync_state=error` and `x_sap_sync_error`, and the job goes to `failed` with retry. A cancel or reverse refused by SAP is **not** auto-retried.
- Payloads are in 6.2. The job uses the same `X-Api-Key` backend as today (`icc.middleware.settings`).

**Monitoring:**
- The job list and dashboard get a "Purchases" filter (the six action names above). This **replaces the "Purchases Sync" page** for the Odoo → SAP direction.
- The existing Middleware Queue view gains an `event_type` filter and shows the six purchase `EventType` rows from `ODOO_WEBHOOK_QUEUE`, with the existing retry button (SAP → Odoo direction).

## 6. Middleware changes (middleware agent)

### 6.1 SAP → Odoo handlers (queue worker)

A new hosted service polls `ODOO_WEBHOOK_QUEUE` for `Status='pending'` rows whose `EventType` is one of the six purchase values, using the same connection, batch, retry and status conventions as the delivery worker. SAP reads use **direct SQL** on the company database (the DI API lock stays free for writes). Odoo writes use JSON-RPC as `sap-integration`.

- **`PO_UPSERT`** (`OPOR`/`POR1`): upsert `x.sap.po` by `docentry`; `status` from `DocStatus`/`CANCELED`.
- **`GRPO_LINK`** (`OPDN`/`PDN1`):
  - upsert `x.sap.grpo` from `OPDN`/`PDN1` (POs from `BaseType=22`), including `line_json`;
  - match `supplier.order` by `container_no` = `U_ShipRef` (normalised like NumAtCard); set `order_id` and add the GRPO to `x_sap_grpo_ids`;
  - write `U_OdooShipmentId` back to the GRPO (DI API) when linked;
  - no match → row `done` with note `NO_ORDER_FOR_SHIPREF`; the order's "Link" action can re-queue it.
- **`AP_INVOICE_CREATE`** (`OPCH`/`PCH1`/`PCH3`):
  1. Idempotency check on `x_sap_invoice_docentry` + `move_type='in_invoice'`.
  2. Partner by `x_sap_cardcode`; if missing, fail the row with `PARTNER_NOT_MAPPED` and create nothing.
  3. Resolve every mapping first (item group → account, VAT group → tax, `PCH3` → G/L map). Any miss fails the row (`ACCOUNT_NOT_MAPPED` / `TAX_NOT_MAPPED`) and creates nothing.
  4. Create the `in_invoice` in `bill_journal_id`:
     - `ref` = `NumAtCard`, `invoice_date` = `DocDate`, `invoice_date_due` = `DocDueDate`, `date` = `DocDate`
     - `currency_id` = `DocCur`, `invoice_currency_rate` per discovery item 4 (`DocRate`)
     - lines: `ItemCode – Dscription`, `quantity`, `price_unit` = net price, `account_id` from the item-group map, `tax_ids` from the VatGroup map, analytic from the map; **no product**
     - `PCH3` expenses as extra lines on the mapped accounts
     - `x_sap_invoice_docentry`, `x_sap_docnum`
  5. Validate the total against `DocTotal` (local) or `DocTotalFC` (foreign) within `total_tolerance`. If it passes, **post**; if not, leave it in draft with `x_sap_sync_error = TOTAL_MISMATCH` and mark the row `done` with that note.
  6. Run the container match (5.3) and set `x_order_ids`, `x_sap_po_ref_ids` and the orders' `x_sap_ap_invoice_ids`.
  7. A service-type A/P invoice is synced the same way, with lines mapped by `AcctCode` through `icc.sap.glaccount.map`.
- **`AP_CREDIT_CREATE`** (`ORPC`/`RPC1`/`RPC3`):
  - `in_refund`, same rules, `x_sap_credit_docentry`, `reversed_entry_id` = the original bill when `BaseType=18` and that bill is synced;
  - service-type credit memos (rebates, bonuses) map lines by `AcctCode`;
  - once posted, call `supplier.account.entry.sync_from_credit_note([move_id])` so the app creates the read-only Credit Note entry.
- **`AP_INVOICE_CANCEL` / `AP_CREDIT_CANCEL`:** as described under Cancellations in section 3. The Odoo reversal goes through the `account.move.reversal` wizard.
- Every handler stores the Odoo id in `ResponseBody` and the error code in `ErrorMessage`.

### 6.2 Odoo → SAP endpoints: payments, refunds, adjustments

All endpoints: `X-Api-Key`, JSON in `snake_case`, the usual `ApiResponse` envelope, **idempotent**, **no SAP internal reconciliation ever**. HTTP 200 with `already_existed: true` when the idempotency key is already in SAP.

**`POST /api/outgoing-payments`** → SAP Outgoing Payment (`oVendorPayments`, object 46) **on account**, no `Invoices` lines.

```json
{
  "odoo_payment_id": 123,
  "odoo_pay_ref": "VBNK1/2026/00042",      // account.payment.name → U_OdooPayRef (idempotency key)
  "card_code": "S00001",
  "doc_date": "2026-11-03",
  "currency": "EUR",
  "rate": 2950.0,                           // SAP DocRate, TZS per EUR
  "amount": 50000.00,                       // TransferSum in doc currency
  "sap_account_code": "1026212",            // TransferAccount, from sap.journal.mapping
  "transfer_reference": "SWIFT-2026-11-03-001",
  "remarks": "Liqui Moly top-up"
}
```
Response: `{ "doc_entry": 101, "doc_num": 2001, "already_existed": false }`.
Idempotency: `SELECT DocEntry FROM OVPM WHERE U_OdooPayRef = @ref AND Canceled = 'N'`.

**`POST /api/outgoing-payments/{doc_entry}/cancel`** → `Payments.Cancel()`; response `{ "cancelled": true, "already_cancelled": false }`. A SAP refusal is returned verbatim as a 409.

**`POST /api/supplier-refunds`** → SAP Incoming Payment (`oIncomingPayments`, object 24) from the vendor CardCode, on account. Same body and response as outgoing payments; `U_OdooPayRef` on `ORCT`. **`/cancel`** as above. (The customer flow's `POST /api/incoming-payments` is untouched.)

**`POST /api/supplier-adjustments`** → SAP Journal Entry (`oJournalEntries`, object 30).

```json
{
  "odoo_ref": "SAE/2026/0007",              // supplier.account.entry name → U_OdooRef (idempotency key)
  "card_code": "S00001",
  "doc_date": "2026-11-03",
  "currency": "EUR",
  "rate": 2950.0,
  "amount": 25.00,                          // positive = credit the supplier (payable up), negative = debit
  "reason": "bank_charges",                 // bank_charges | small_balance
  "sap_gl_code": "6011510",                 // mirror G/L from icc.sap.adjustment.map
  "memo": "Bank charges on SWIFT 2026-11-03"
}
```
Lines: BP line `ShortName = card_code` and G/L line `AccountCode = sap_gl_code`, opposite sides, FC amounts in `currency` at `rate`. Response: `{ "jdt_docentry": 5001, "already_existed": false }`.
Idempotency: `OJDT.U_OdooRef`.

**`POST /api/supplier-adjustments/{jdt_docentry}/reverse`** → `JournalEntries.Cancel()` (SAP posts the storno); the reversal carries `U_OdooRef = "<odoo_ref>:REV"`. Response `{ "reversal_docentry": 5002, "already_existed": false }`.

Failures: the endpoint returns the SAP message; the control centre job records it, sets `error` on the record, and retries per its own rules (never for a refused cancel or reverse).

### 6.3 Odoo → SAP: landed costs (Phase 4)

**`POST /api/landed-costs`** → SAP Landed Costs document via `LandedCostsService`.

```json
{
  "odoo_shipment_id": "SO-2026-0012",       // supplier.order reference → U_OdooShipmentId (idempotency key)
  "ship_ref": "MSKU1234567",                // container no. → U_ShipRef
  "grpo_docentry": 345,
  "previous_lc_docentry": null,             // set for a delta push
  "cost_lines": [ { "lc_code": 2, "amount_tzs": 1250000.00 }, { "lc_code": 8, "amount_tzs": 180000.00 } ]
}
```
- Item lines are based on the container's GRPO lines; one cost line per `lc_code` in TZS; `U_OdooShipmentId`, `U_ShipRef`; **no Vendor/Broker**.
- Idempotency on `OIPF.U_OdooShipmentId` plus the set of amounts (a delta push with new amounts is a new document whose `U_OdooShipmentId` is `"<ref>:<n>"`).
- **Guards:** never push with no GRPO, never push a negative total for a code without manual approval, never push a code that is not capitalised. Confirm the entity and field names against the DI API `LandedCostsService` (`LandedCostParams`, `LandedCost_ItemLines`, `LandedCost_CostLines`).
- Response: `{ "doc_entry": 77, "doc_num": 1007, "already_existed": false }`. The control centre write-back posts the Odoo clearing JE (5.7).

### 6.4 Configuration

- Mapping and settings are the control centre models in 5.7, read over JSON-RPC with a 5-minute cache; nothing is duplicated in Neon.
- Middleware `appsettings`: `PurchaseSync:Enabled`, `PurchaseSync:PollingIntervalSeconds` (60), `PurchaseSync:BatchSize` (10), `PurchaseSync:MaxRetries` (5). The queue connection is the existing `WebhookQueue:ConnectionString`.

### 6.5 Monitoring and backfill

- The queue endpoints `GET /api/webhook-queue` and `/summary` accept `event_type` so the control centre's queue view can show purchase rows; retry endpoints are unchanged.
- `POST /api/admin/setup-sap-udfs` (4.1).
- Backfill: `GET /api/purchases/backfill?from=<cutover>&dry_run=true` produces a CSV of open POs, GRPOs, A/P invoices and credit memos dated on or after the cut-over date; `dry_run=false` enqueues them with the right `EventType`. Nothing before cut-over is queued, re-paid or re-allocated.

### 6.6 Cut-over rules (middleware)

- The middleware reads `cutover_date` from `icc.ap.sync.settings`. **While it is empty, the purchase worker processes nothing** and logs that it is waiting for the cut-over date.
- SAP documents dated before the cut-over date are marked `done` with note `BEFORE_CUTOVER` and are not created in Odoo.

## 7. Month-end (Phase 5)

1. **Inventory adjustment** (periodic): from the SAP closing valuation, which includes capitalised landed costs. Unallocated import costs already sit on the Odoo clearing accounts.
2. **GRNI accrual:** value of GRPOs not yet invoiced. Dr Purchases / Cr GRNI, auto-reversing.
3. **FX revaluation** of open foreign-currency supplier balances (Odoo unrealised FX adjustment).
4. **Prepayment reclass (optional):** if a supplier payable is in debit at month end, reclass it to a prepayment account for the balance sheet, and reverse it on day 1.
5. **Reconciliation reports:**
   - supplier balance per CardCode, SAP against Odoo (must match);
   - Odoo clearing balances by container;
   - SAP 20154xx credits against Odoo allocated amounts, per code;
   - allocations in `pending_sync` or `error`.

## 8. Phasing

One PR per phase and per repo. Docs are updated in the same PR as each change.

| Phase | Scope | Owner | Depends on |
|---|---|---|---|
| 0 | Discovery script and report (5.0) | Odoo agent | — |
| 1 | Middleware: delivery-worker `ObjectType='15'` filter, `SBO_SP_TransactionNotification` migration, UDF routine, purchase queue worker with `PO_UPSERT`, `GRPO_LINK`, invoice and credit memo sync (auto-post) and cancels, container matching, backfill dry-run, queue `event_type` filter. Odoo: 5.2 fields and models, 5.7 mapping and settings models, queue view filter | Both | 0 |
| 2 | Shipments cost lines and TANSAD → Odoo bills; cost-line field changes; cut-over | Odoo agent | 0 (can run in parallel with 1) |
| 3a | Middleware endpoints: outgoing payments, supplier refunds, supplier adjustments, cancel and reverse (6.2) | Middleware agent | 1 |
| 3b | Account Activity → Odoo payments, refunds, adjustments; allocation mirroring and locking; control-centre automated actions, jobs and write-backs (5.4, 5.7) | Odoo agent | 3a |
| 4 | Landed cost push (Flow E): `POST /api/landed-costs` and the control-centre job and JE | Both | 2, 3b |
| 5 | Month-end reports and JEs; monitoring completed | Both | 4 |

## 9. Acceptance tests

1. **EUR A/P invoice** (3 lines, VAT, freight): the Odoo bill auto-posts with matching totals and rate = `DocRate`. Re-queueing creates no duplicate. A total mismatch leaves it in draft with a flag.
2. **No stock impact:** no `stock.move` records, and the Stock Interim accounts are unchanged.
3. **Top-up before any invoice** (€50,000 Payment entry):
   - the Odoo payment posts immediately, even with the middleware stopped; the job retries and succeeds once it is back;
   - SAP gets an Outgoing Payment on account with `U_OdooPayRef`; re-running the job returns `already_existed` and creates nothing;
   - the SAP and Odoo supplier balances match.
4. **FIFO across invoices:** two bills sync (€30,000 and €40,000):
   - allocations €30,000 and €20,000 become partial reconciliations and are `locked`;
   - FX differences are posted;
   - nothing is reconciled in SAP.
5. **Back-dated payment** entered after the locks: the locked allocations are unchanged, and only the remaining capacity is allocated.
6. **Manual allocation** to an in-process order with no bill yet: `pending_sync`, which mirrors once the bill syncs.
7. **Cancelling a payment:**
   - the Odoo partials are removed;
   - the allocations return to `pending_sync` and FIFO re-runs;
   - the SAP Outgoing Payment is cancelled and the Odoo payment keeps its SAP keys with state `cancelled`.
8. **Rebate credit memo** entered in SAP (service type): an Odoo credit note posts, a read-only Credit Note entry appears in the app, and FIFO allocates it. Users cannot create Credit Note entries.
9. **Refund:** an Odoo inbound payment, and a SAP Incoming Payment on account.
10. **Bank charges adjustment of €25:**
    - Odoo JE Dr Bank Charges / Cr payable;
    - SAP JE to the business partner;
    - the balances still match.
11. **Small-balance adjustment of €12** with the limit at €10: blocked.
12. **Cost bills and TANSAD:**
    - the bills post to the correct clearing, Input VAT and expense accounts;
    - an unmapped TANSAD head errors;
    - goods lines on the order create no bill.
13. **Landed costs:**
    - "Costs Final" produces a SAP Landed Costs document on the container's GRPO, in TZS, with no vendor;
    - SAP item cost rises;
    - the Odoo clearing JE brings the container's clearing balance to 0;
    - a late cost bill produces only a delta.
14. **Container auto-link:** a GRPO with `U_ShipRef` links to the order, and the A/P invoice links by `NumAtCard`, including one invoice covering two containers.
15. **UDF routine** run twice creates nothing the second time, and it refuses to run while users are logged in.
16. **Queue isolation:** a delivery row and a purchase row pending at the same time are each processed by their own worker only; a missing mapping fails the purchase row with its code and the row succeeds on retry after the mapping is added.
17. **Cut-over:** with `cutover_date` empty nothing is processed; an A/P invoice dated before the cut-over is marked `BEFORE_CUTOVER` and creates nothing.

## 10. Documentation

In the same PR as each change, update:
- the middleware docs (README, `PROJECT_HANDOFF.md`, runbook): UDFs, queue `EventType` values, workers, endpoints, backfill, error codes;
- the control centre docs: mapping models, automated actions, job write-backs, queue view;
- the Shipments app docs: entry types, allocation locking, cut-over.

Also write a short user guide for the accounts team:
- recording top-ups, refunds and the two adjustment types;
- what "pending sync" means and where to see failed jobs;
- pressing "Costs Final";
- fixing sync errors (adding a missing mapping, retrying a queue row or a job).

## 11. Open items and risks

1. **Auditor confirmation** of the FX policy (4.4).
2. **Accounts to confirm:**
   - the Odoo mirror account codes, including Bank Charges and Small Balance Adj.;
   - the SAP bank-charges G/L;
   - the import VAT treatment;
   - the cut-over date (set at go-live; see 5.5);
   - the small-balance limit (default €10).
3. **Accounts to add the missing landed cost codes in SAP** before Phase 4. This includes Excise Duty.
4. **Item weights maintained?** This decides the allocation basis for shipping and transport.
5. **Pricing:** until Phase 4 is live, SAP item cost excludes landed costs. Once it is live, any pricing rule that adds a landing-cost uplift on top of SAP cost (e.g. × 1.25) would double-count. Review the pricing rules for Lubes items before Phase 4 goes live.
6. **SAP supplier aging is not meaningful,** because there is never any SAP reconciliation. Use Odoo or the app for aging. The SAP supplier balance remains correct.
7. **Resolved in v4:** Odoo → SAP transport (control centre jobs), `account.move` field reuse, mapping location (control centre), queue table (`ODOO_WEBHOOK_QUEUE`), DI API build (10.00.280, 64-bit).
