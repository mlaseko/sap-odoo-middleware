# Purchases sync runbook (SAP B1 → Odoo, Molas Lubes)

Spec: [`molas_purchases_integration_spec.md`](molas_purchases_integration_spec.md) (v4). This page is the
operational side of Phase 1: what runs, how to deploy it, and what each error code means.

Go-live, in order: [`purchases-go-live-checklist.md`](purchases-go-live-checklist.md).

## What runs

| Piece | Where | Purpose |
|---|---|---|
| `SBO_SP_TransactionNotification` | SQL Server, `Molas_Lubes_LTD` | Queues POs (22), GRPOs (20), A/P invoices (18) and A/P credit memos (19) into `ODOO_WEBHOOK_QUEUE` with an explicit `EventType`. Migration: `migrations/sqlserver/2026-10-20__purchases_transaction_notification.sql`. |
| `WebhookQueueProcessor` | middleware | Delivery confirmations. Now reads **only `ObjectType = '15'`** rows. |
| `PurchaseSyncQueueProcessor` | middleware | Reads only the six purchase `EventType` rows. Polls every `PurchaseSync:PollingIntervalSeconds` (60). |
| `PurchaseSyncHandlers` | middleware | `PO_UPSERT` → `x.sap.po`; `GRPO_LINK` → `x.sap.grpo` + container link + `U_OdooShipmentId` stamp; `AP_INVOICE_CREATE` / `AP_CREDIT_CREATE` → vendor bill / credit note, posted when totals match; `AP_INVOICE_CANCEL` / `AP_CREDIT_CANCEL` → cancel draft, reverse posted-unreconciled, flag `CANCEL_AFTER_ALLOCATION` otherwise. |
| `IccMappingCache` | middleware | Reads `icc.ap.sync.settings`, `icc.sap.vatgroup.map`, `icc.sap.itemgroup.map`, `icc.sap.glaccount.map` from Odoo, cached 5 minutes. |
| `SapUdfSetupService` | middleware | The UDF routine with the exclusive-access check. |

Reads from SAP are direct SQL on the company database (`SapB1:Server` / `CompanyDb` with `DbUserName` / `DbPassword`, or
`PurchaseSync:SapSqlConnectionString`). Writes to Odoo go through `OdooRpcClient` as the `sap-integration` user
(`Odoo:ApiKey`). The only DI-API write in this flow is the GRPO stamp.

## Deploy order (Phase 1)

1. Deploy the middleware build that contains the `ObjectType = '15'` filter in `WebhookQueueProcessor`. Restart the service and
   confirm deliveries still flow.
2. Odoo: install the control-centre models of spec §5.7 and the fields of §5.2 (Odoo agent). Create the
   `icc.ap.sync.settings` record with `bill_journal_id` set and **leave `cutover_date` empty** until go-live.
3. Run the UDF routine out of hours: `POST /api/admin/setup-sap-udfs?dry_run=true` to see who is connected, then
   `POST /api/admin/setup-sap-udfs`. It refuses while other sessions are connected; `force=true` overrides once you have
   checked the list (a session flagged `likely_this_middleware` is this service's own DI-API connection).
4. Apply the SQL migration in SSMS against `Molas_Lubes_LTD`. Safe to re-run.
5. Fill the mapping tables in Odoo (Integration → Configuration → SAP Purchases). `POST /api/purchases/mappings/refresh`
   shows what the middleware sees.
6. At go-live set `cutover_date`. Until then the worker logs "waiting for cut-over date" every 10 minutes and processes nothing.
7. Backfill: `GET /api/purchases/backfill` (CSV dry run), then `POST /api/purchases/backfill`.

## Endpoints

| Method | Path | Notes |
|---|---|---|
| `POST` | `/api/admin/setup-sap-udfs?dry_run=&force=` | UDF routine (also `POST /api/sapb1/setup-udfs`, same service). 409 when refused. |
| `GET` | `/api/purchases/backfill?from=YYYY-MM-DD&types=22,20,18,19` | CSV dry run. `from` defaults to the cut-over date and may not be before it. |
| `POST` | `/api/purchases/backfill?from=&types=` | Queues the candidates not already queued (TransType `A`). |
| `POST` | `/api/purchases/mappings/refresh` | Drops the mapping cache and reloads. |
| `GET` | `/api/webhook-queue?status=&event_type=` | Queue rows; `event_type` filters to one of the six purchase values (or `webhook`, `resync`). |
| `GET` | `/api/webhook-queue/summary?event_type=` | Counts by status. |
| `POST` | `/api/webhook-queue/{id}/retry` | Sets a failed row back to pending (works for purchase rows too). |
| `POST` | `/api/outgoing-payments` | Phase 3a. SAP Outgoing Payment to the supplier **on account** (OVPM, no invoice lines). Idempotent on `U_OdooPayRef`; a cancelled payment with the same reference → 409. |
| `POST` | `/api/outgoing-payments/{docEntry}/cancel` | `Payments.Cancel()`; already cancelled → 200 with `already_cancelled`; SAP refusal → 409 (no automatic retry). |
| `POST` | `/api/supplier-refunds` | Phase 3a. SAP Incoming Payment from the vendor on account (ORCT, `DocType` supplier). Same body and rules. |
| `POST` | `/api/supplier-refunds/{docEntry}/cancel` | As above for ORCT. |
| `POST` | `/api/supplier-adjustments` | Phase 3a. Journal entry: business-partner line (`ShortName = card_code`) and the mirror G/L line, sides from `direction`, FC amount at `rate`. Idempotent on `U_OdooRef`. Returns `jdt_docentry` (OJDT.TransId). |
| `POST` | `/api/supplier-adjustments/{jdtDocEntry}/reverse` | SAP storno (`JournalEntries.Cancel()`), reversal stamped `U_OdooRef = "<ref>:REV"`. Idempotent on `OJDT.StornoToTr`; refusal → 409. |
| `GET` | `/api/month-end/supplier-balances?as_of=` | Phase 5. Every supplier's balance at the date from the journal (JDT1), credit-positive, per currency. |
| `GET` | `/api/month-end/grni?as_of=` | Phase 5. Goods received at the date and not invoiced or returned at the date, per GRPO, local currency. |
| `GET` | `/api/month-end/inventory-valuation?as_of=` | Phase 5. SAP's perpetual inventory value at the date (cumulative OINM, includes capitalised landed costs), by warehouse. |
| `GET` | `/api/month-end/landed-cost-credits?from=&to=` | Phase 5. Credits per landed cost code (JDT1 transaction type 69 on the OALC allocation accounts) and the OIPF documents of the period; defaults to the calendar month of `to`. |
| `POST` | `/api/landed-costs` | Phase 4. SAP Landed Costs document (OIPF) through `LandedCostsService`: item lines from the container's GRPO (first push) or from the previous landed cost document (delta), one cost line per `lc_code` in TZS, no vendor. Idempotent on `U_OdooShipmentId` (`ref` / `ref:n`); guards → 422. |

All endpoints need `X-Api-Key`.

## Queue row outcomes

`ResponseBody` holds the handler's JSON note on `done`; `ErrorMessage` holds `CODE: message` on `failed`.

| Code / note | Meaning | Action |
|---|---|---|
| `BEFORE_CUTOVER` | Document dated before `cutover_date`. Nothing created. | None. |
| `CANCELLATION_DOC_SKIPPED` | The row was the cancellation document itself (`CANCELED = 'C'`). | None; the original's `C` event drives the reversal. |
| `ALREADY_SYNCED` | Idempotency hit: the move already exists and is not draft. | None. |
| `NO_ORDER_FOR_SHIPREF` | GRPO's `U_ShipRef` matched no `supplier.order.container_no`. `x.sap.grpo` was still written. | Fix the container number on the order or the GRPO, then retry the row (or use the order's "Link" action). |
| `PARTNER_NOT_MAPPED` (failed, terminal) | No `res.partner.x_sap_cardcode` = the SAP CardCode. | Set the CardCode on the supplier, retry the row. |
| `ACCOUNT_NOT_MAPPED` (failed, terminal) | Item group / G/L / expense account has no mapping. The message names the key. | Add the mapping, `POST /api/purchases/mappings/refresh`, retry the row. |
| `TAX_NOT_MAPPED` (failed, terminal) | SAP VatGroup has no `icc.sap.vatgroup.map` entry. | Same. |
| `TOTAL_MISMATCH` (done, bill in draft) | Odoo `amount_total` differs from SAP `DocTotal`/`DocTotalFC` by more than the tolerance. `x_sap_sync_error` set, chatter note posted. | Accounts reviews the draft bill in Odoo. |
| `CANCEL_AFTER_ALLOCATION` (done) | SAP cancelled a document that is already reconciled in Odoo. Nothing changed. | Accounts unreconciles and reverses by hand. |
| `BILL_JOURNAL_MISSING` / `ICC_SETTINGS_MISSING` (retrying) | Control-centre settings not readable or incomplete. | Fix the settings record; the row retries on its own. |
| `SP_FAILED_ROW` (failed) | Row with DocEntry 0 written by the stored procedure's CATCH block. | Read its original `ErrorMessage`; nothing to sync. |
| `CURRENCY_NOT_FOUND` / `CURRENCY_INACTIVE` | Document currency missing or archived in Odoo. | Activate the currency, retry. |

Transient errors (Odoo or SQL unreachable) retry up to `PurchaseSync:MaxRetries` (5) and then stay `failed` until retried by hand.

## Odoo → SAP endpoints (Phase 3a)

Called by the control-centre jobs of `molas_sap_purchase_icc` (bodies in spec v4 §6.2). Behaviour worth knowing:

- **On account means no `Invoices` rows.** SAP books the payment against the business partner with no invoice
  applied and no internal reconciliation; the SAP supplier balance is right, SAP's invoice aging is not used.
- **`rate` is `DocRate`** (TZS per EUR) and is written on the payment. For a journal entry in EUR the lines carry the
  FC amount and the LC amount = amount × rate. SAP still requires an exchange rate for that currency and date in its
  rate table (Administration ▸ Exchange Rates); without it SAP refuses the document and the job fails with SAP's message.
- **Validation:** missing `card_code`, `odoo_pay_ref` / `odoo_ref` (max 50 characters), `sap_account_code` /
  `sap_gl_code`, a non-positive `amount` or an unknown `direction` → 400 before SAP is touched.
- **Idempotency:** the UDFs must exist (`POST /api/admin/setup-sap-udfs`). If the UDF is missing the check fails
  closed: nothing is created and the job shows the message. A re-sent reference whose SAP payment was cancelled →
  409 (Odoo must send a new payment with a new reference).
- **Cancels and reversals** return 409 on any SAP refusal so the control centre does not retry them automatically.
- Responses are the usual envelope: `{"success": true, "data": {"doc_entry": 101, "doc_num": 2001, "obj_type": "46", "already_existed": false}}`,
  `{"cancelled": true, "already_cancelled": false, ...}`, `{"jdt_docentry": 5001, "number": 9001, "already_existed": false}`,
  `{"original_docentry": 5001, "reversal_docentry": 5002, "reversal_number": 9002, "already_existed": false}`.

## Odoo → SAP landed costs (Phase 4)

Called by the control centre's `landed_cost_push` job (`molas_sap_purchase_icc`) when a container order reaches *Costs
final* (body in spec v4 §6.3). Behaviour worth knowing:

- **One SAP document per push.** The first push is based on the GRPO's lines (base type 20). A late cost bill puts the
  order back to *Costs final*; the next push is a **delta** based on the previous landed cost document (base type 69,
  `previous_lc_docentry`) with only the difference per code, keyed `"<ref>:<n>"`. The control centre computes the
  difference from the clearing-account balances, so nothing is sent twice.
- **Codes** must exist in SAP (Administration ▸ Setup ▸ Purchasing ▸ Landed Costs, `OALC`) with an allocation account;
  `LC_CODE_UNKNOWN` names the missing one. The code is text (`x_sap_lc_code` on the Odoo expense category).
- **Negative deltas** (a credit note on a cost bill after the push) need the order's *negative landed cost approved*
  tick; otherwise `NEGATIVE_NOT_APPROVED`. A first push is never negative.
- **Guards are 422** and are not retried automatically: fix the cause in Odoo or SAP and press *Costs Final* again
  (the order is in `error` with `x_lc_error`). SAP's own refusals (period closed, no allocation account on the code) are
  500 with SAP's message and retry per the job's rules.
- **Idempotency** is `OIPF.U_OdooShipmentId`; the UDF must exist (`POST /api/admin/setup-sap-udfs`). A re-sent key
  returns the existing document with `already_existed`.
- Response: `{"success": true, "data": {"doc_entry": 77, "doc_num": 1007, "idempotency_key": "SO-2026-0012", "item_lines": 2, "cost_total": 1430000.0, "already_existed": false}}`.

## Month-end (Phase 5)

The control centre's month-end record (Integration ▸ Configuration ▸ SAP Purchases ▸ Month-end, `molas_sap_purchase_icc`)
reads the four `GET /api/month-end/*` endpoints for the period's last day and compares them with Odoo:

- **Supplier balances per CardCode** (must match): SAP's journal balance against Odoo's payable balance of the partner
  with that CardCode, in EUR and TZS.
- **GRNI:** the open goods-receipt value becomes the accrual (Dr Purchases / Cr GRNI) with its reversal on day 1.
- **Inventory:** SAP's perpetual value against the Odoo inventory account; the difference is the month-end adjustment.
- **Landed cost credits per code** against the Odoo clearing entries of the period, and the clearing balance by container.
- **Issues:** payments, adjustments and containers in `pending` / `error`, failed purchases jobs, failed queue rows.

All reads are plain SQL on the company database; nothing is written to SAP. `as_of` defaults to today; the period of
the landed cost credits defaults to the calendar month of `to`. The GRNI query joins every GRPO line to its invoices
and returns, so give it a moment on a large database (300 s command timeout).

## Configuration

```jsonc
"PurchaseSync": {
  "Enabled": true,
  "PollingIntervalSeconds": 60,
  "BatchSize": 10,
  "MaxRetries": 5,
  "MappingCacheMinutes": 5,
  "SapSqlConnectionString": "",          // optional; default built from SapB1:*
  "InvoiceRateIsInverseOfDocRate": true  // flip only if discovery item 5.0/4 shows the other direction
}
```

The queue connection is `WebhookQueue:ConnectionString` (shared with the delivery worker).

## Contract with the Odoo side (what the middleware reads and writes)

- Reads: `res.partner.x_sap_cardcode`; `supplier.order` (`id`, `name`, `container_no`, `invoice_no`); `icc.ap.sync.settings`
  (`total_tolerance`, `cutover_date`, `bill_journal_id`, `small_balance_limit`); `icc.sap.vatgroup.map` (`sap_vat_group`, `tax_id`);
  `icc.sap.itemgroup.map` (`sap_item_group`, `account_id`, `analytic_account_id`); `icc.sap.glaccount.map` (`sap_gl_code`, `account_id`);
  `account.journal.company_id`, `res.company.currency_id`, `res.currency.name`.
- Writes: `account.move` (`move_type`, `partner_id`, `journal_id`, `currency_id`, `ref`, `invoice_date`, `date`, `invoice_date_due`, `narration`,
  `invoice_currency_rate`, `x_sap_invoice_docentry` or `x_sap_credit_docentry`, `x_sap_docnum`, `x_sap_objtype`, `x_sap_sync_error`,
  `reversed_entry_id`, `invoice_line_ids` with `name`, `quantity`, `price_unit`, `account_id`, `tax_ids`, `analytic_distribution`);
  `x.sap.grpo.odoo_shipment_written`.
- SAP (Phase 4): `OIPF` documents with `U_OdooShipmentId` = `supplier.order.name` (or `name:n`), `U_ShipRef` = the container number.
- Calls (model methods of `molas_sap_purchase`, keyword arguments): `x.sap.po.upsert(vals=)`, `x.sap.grpo.upsert(vals=, lines=)`,
  `account.move.sap_match(move_id=)`; and `account.move.action_post`, `account.move.button_cancel`, `account.move.message_post(body=)`,
  `account.move.reversal` (create + `reverse_moves`), `supplier.account.entry.sync_from_credit_note(move_ids=[id])` (Phase 3, Odoo side).
  The container and PO links on bills and GRPOs are made by those Odoo methods, not by the middleware.
- SAP: `OPDN.U_OdooShipmentId` = `supplier.order.name` (DI API) when a GRPO links to an order.
