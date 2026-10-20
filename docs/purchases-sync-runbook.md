# Purchases sync runbook (SAP B1 → Odoo, Molas Lubes)

Spec: [`molas_purchases_integration_spec.md`](molas_purchases_integration_spec.md) (v4). This page is the
operational side of Phase 1: what runs, how to deploy it, and what each error code means.

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
- Writes: `x.sap.po` (`docentry`, `docnum`, `partner_id`, `currency_id`, `doc_total`, `doc_date`, `due_date`, `status`, `num_at_card`);
  `x.sap.grpo` (`docentry`, `docnum`, `po_ids`, `ship_ref`, `doc_date`, `doc_total_lc`, `order_id`, `line_json`);
  `supplier.order.x_sap_grpo_ids`, `supplier.order.x_sap_ap_invoice_ids`;
  `account.move` (`move_type`, `partner_id`, `journal_id`, `currency_id`, `ref`, `invoice_date`, `date`, `invoice_date_due`, `narration`,
  `invoice_currency_rate`, `x_sap_invoice_docentry` or `x_sap_credit_docentry`, `x_sap_docnum`, `x_sap_sync_error`, `x_order_ids`,
  `x_sap_po_ref_ids`, `reversed_entry_id`, `invoice_line_ids` with `name`, `quantity`, `price_unit`, `account_id`, `tax_ids`, `analytic_distribution`).
- Calls: `account.move.action_post`, `account.move.button_cancel`, `account.move.message_post(body=)`,
  `account.move.reversal` (create + `reverse_moves`), `supplier.account.entry.sync_from_credit_note(move_ids=[id])` (an `@api.model`
  method the Shipments app exposes; keyword argument).
- SAP: `OPDN.U_OdooShipmentId` = `supplier.order.name` (DI API) when a GRPO links to an order.
