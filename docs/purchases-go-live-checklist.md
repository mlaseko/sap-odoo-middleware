# Purchases integration: go-live checklist (Molas Lubes)

Spec: [`molas_purchases_integration_spec.md`](molas_purchases_integration_spec.md) (v4). Runbook:
[`purchases-sync-runbook.md`](purchases-sync-runbook.md). All five phases are merged on both repositories; this page
is the order of the remaining steps, what each one verifies, and what "done" looks like. Nothing below needs new code.

Every call needs the `X-Api-Key` header. `<mw>` is the middleware base URL.

## 0. The cut-over date decides everything: set it last

`icc.ap.sync.settings.cutover_date` (Integration ▸ Configuration ▸ SAP Purchases ▸ Settings) is read by both sides:

| Side | What the date does |
|---|---|
| Middleware worker | processes nothing while empty; creates Odoo bills, POs and GRPOs only for SAP documents dated on or after it |
| Backfill | `GET/POST /api/purchases/backfill` queues every open PO, GRPO, A/P invoice and credit memo dated on or after it |
| Shipments app | every cost line dated on or after it becomes "to bill" (a vendor bill per vendor invoice and TANSAD); account entries dated on or after it are "live" (Odoo payments and adjustments, pushed to SAP) |
| Landed costs | every container whose costs are billed becomes eligible for a SAP Landed Costs document |

**A date in the past re-processes history.** With 1 January 2026 the backfill queues ten months of SAP documents into
Odoo, every 2026 container shows its costs as "to bill", and every 2026 payment in the app becomes a live entry.
Unless accounts have decided on a full 2026 re-processing (with the Balance C/F at that date and no existing Odoo
bookings for those documents), set the date to **the first day of the month after the test walk** (spec 5.5), and
only after steps 1 to 6 below. Until then leave it empty: the system is safe with the date empty.

## 1. Deploy the merged code

- **Middleware:** build and deploy `main` on the Windows box (last merge: Phase 5, `GET /api/month-end/*`). Confirm
  with `GET <mw>/api/month-end/inventory-valuation` → `{"success": true, ...}` (an older build answers 404).
- **Odoo:** upgrade `integration_control_center` (19.0.3.25.1), `molas_sap_purchase` and `molas_sap_purchase_icc`
  (19.0.2.0.0). The menu Integration ▸ Configuration ▸ SAP Purchases must show *Month-end*.
- Run the bridge tests once on the instance: `--test-tags molas_sap_purchase_icc`.

## 2. Prepare SAP (out of hours, SAP users logged off)

1. `POST <mw>/api/admin/setup-sap-udfs?dry_run=true` → lists the connected SAP sessions. The routine refuses while
   others are connected; `force=true` overrides once the list is checked (a session flagged `likely_this_middleware`
   is the service itself).
2. `POST <mw>/api/admin/setup-sap-udfs` → creates `OPDN.U_ShipRef`, `OPDN.U_OdooShipmentId`, `OIPF.U_OdooShipmentId`,
   `OIPF.U_ShipRef`, `OVPM.U_OdooPayRef`, `ORCT.U_OdooPayRef`, `OJDT.U_OdooRef`. Re-run until it reports nothing
   to create. Until these exist every Odoo → SAP push fails closed on purpose.
3. In SSMS, against `Molas_Lubes_LTD`, run `migrations/sqlserver/2026-10-20__purchases_transaction_notification.sql`
   (safe to re-run). It adds the blocks for objects 22, 20, 18, 19 to `SBO_SP_TransactionNotification` and the index
   on `ODOO_WEBHOOK_QUEUE`, and refuses a new goods receipt without the container number in `U_ShipRef` (error
   42100), the field that links it to its container order.
4. Verify: add a remark to any purchase order in SAP and run
   `SELECT TOP 5 * FROM ODOO_WEBHOOK_QUEUE WHERE EventType = 'PO_UPSERT' ORDER BY Id DESC` → one new `pending` row.
   The middleware leaves it `pending` with "waiting for cut-over date" in its log while the date is empty.
5. Landed cost codes: Administration ▸ Setup ▸ Purchasing ▸ Landed Costs must hold every capitalised code with an
   allocation account, **including Excise Duty** (spec 4.3 and open item 3). Note each code's text.
6. Exchange rates: SAP needs an EUR rate for every posting date (Administration ▸ Exchange Rates); a payment or
   journal entry in EUR fails without it.

## 3. Fill the control centre (Integration ▸ Configuration ▸ SAP Purchases)

| Page | Fill | Check |
|---|---|---|
| VAT Groups | every SAP VatGroup used on A/P documents → Odoo purchase tax | `POST <mw>/api/purchases/mappings/refresh` shows the counts |
| Item Groups | every SAP item group → Odoo expense/stock account (+ analytic) | same |
| G/L Accounts | every SAP G/L code used on service lines → Odoo account | same |
| Adjustment Reasons | `bank_charges`, `small_balance` → Odoo account and SAP G/L code | both rows have a SAP code |
| Settings | bill journal, tolerance, small-balance limit; landed cost clearing journal and offset account; month-end journal, GRNI accrual and expense, inventory and change-in-inventory, supplier prepayment | all set; **cut-over date still empty** |

The sap-integration user (spec 5.6) must exist with its API key in the backend record, and `Odoo:ApiKey` in the
middleware configuration must be that key.

## 4. Fill the Shipments app side

- Supplier Orders ▸ SAP Integration ▸ **Setup check**: run it; every account it lists must be "found", every supplier
  must have a CardCode, every capitalised category an account and a SAP code (*Propose SAP mapping* fills the
  proposals). Fix what it flags before going on.
- Supplier Orders settings: TRA – Customs partner, purchase journal, default VAT tax, adjustment journal, bank-charges
  and small-balance accounts.
- Suppliers: `x_sap_cardcode` on every SAP vendor (the payments and the month-end balance comparison key on it).
- Expense categories: `x_capitalised` and `x_sap_lc_code` (the SAP code text) on the landed cost categories.

## 5. Accounts sign-off (spec 11)

FX policy confirmed by the auditor (4.4); mirror account codes; SAP bank-charges G/L; import VAT treatment; small-balance
limit; item weights maintained if freight is allocated by weight; pricing rules reviewed so a landing uplift is not
applied twice once landed costs sit in SAP cost. Prepare the **Balance C/F** entry per supplier as at the cut-over date.

## 6. Test walk on the test company or one container

With the date still empty nothing runs by itself, so the test is done by hand:

1. `GET <mw>/api/purchases/backfill?from=<planned cut-over>` → the CSV of what the backfill would queue. Read it with
   accounts: it is the list of SAP documents Odoo will receive.
2. `POST <mw>/api/outgoing-payments` with a small test body against the SAP **test** company (or a payment you will
   cancel): the response carries `doc_entry`; re-sending the same body returns `already_existed: true`.
3. A landed cost push on one container in the test company: `POST <mw>/api/landed-costs` with its GRPO and one code.
   Then read the middleware log for the lines
   `LandedCostAllocationByEnum has no member for` or `no amount property on LandedCost_CostLine`: if either appears,
   send the `Members:` list it prints and the real names are added to the candidates. This is the one DI API detail
   not yet confirmed on this build.
4. Month-end: create a month-end record for the last closed month and press *Fetch from SAP*; read the Supplier
   balances tab with accounts (every mismatch is a timing difference or a real one).

## 7. Go-live, in this order

1. Set `cutover_date` (first of the month). From the next poll the worker processes queue rows dated on or after it.
2. Post the Balance C/F entries.
3. `POST <mw>/api/purchases/backfill?from=<cutover>` (after the dry run of step 6.1). Watch
   `GET <mw>/api/webhook-queue/summary?event_type=AP_INVOICE_CREATE` (and the other five types) until `pending` is 0;
   `failed` rows are explained in the runbook's outcome table.
4. Enable the five automated actions (Integration ▸ Configuration ▸ Automated Actions): *SAP purchases: push supplier
   payment / refund*, *cancel supplier payment / refund*, *push supplier adjustment*, *reverse supplier adjustment*,
   *push landed costs*.
5. First real container end to end: GRPO linked → cost bills → Costs Final → SAP Landed Costs document → clearing
   entry. Compare the SAP document with the clearing entry.
6. First month-end with the record; keep the periodic inventory valuation sync or the month-end adjustment, not both
   blindly (the adjustment re-reads the Odoo balance, so they coexist, but accounts should know which one moved the
   inventory account).

## Done means

- Queue summary shows no `pending` older than two polls and every `failed` row has a known cause.
- Integration ▸ Jobs, filter *SAP Purchases – failed*: empty.
- The month-end Supplier balances tab matches for every CardCode, or every mismatch is explained.
