# Middleware response: receiving a shipment as several GRPOs and one A/P invoice draft

**To:** Data-Sync-Hub, Receive Shipment page.
**From:** SAP middleware (`/api/autohub/inv/*`, X-Api-Key).
**Re:** MIDDLEWARE_RECEIVE_SHIPMENT.md, the decisions confirmed with finance in Nov 2026.
**Status:** all three changes and the optional status route are **live** (16 Nov 2026), tested end to end on
MOLAS_Live_2021.

You can turn on **`SAP_GRPO_SEND_REFERENCE=1`** now.

## Live test

PO 235 (Germax, VEN1011, 13 lines, TZS, warehouse 003) was received as two cartons:

| Step | Result |
|---|---|
| GRPO carton 1 (lines 0–5) | GRPO 2552, `NumAtCard` "GAPC130726 / Ctn 1", remarks set, branch 1, TZS 4,414,500 |
| GRPO carton 2 (lines 6–12) | GRPO 2553, TZS 3,132,000. PO 235 closed |
| GRPO replay (same `app_ref` and body) | GRPO 2552 returned, `already_existed: true`, nothing posted |
| Draft from 2552 + 2553 | Draft 61 (DocEntry 222): 13 lines, TZS 7,546,500 = the two GRPOs |
| Draft replay | Same draft, `already_existed: true` |
| Same `num_at_card`, other `app_ref` | 409, nothing created |
| Finance added the draft in SAP | A/P invoice 61. The status route answers `added` with invoice 61 |

## 1. `POST /grpo`

Request: as in your spec. New optional header fields:

- `num_at_card` → `OPDN.NumAtCard` (cut to 100 characters).
- `comments` → `OPDN.Comments` (cut to 254 characters).

Behaviour:

- **Branch:** SAP needs a branch on every GRPO. The middleware takes it from the receiving warehouses, so you
  send nothing. If a warehouse has no active branch → 422. If the lines' warehouses are in different branches
  → 400 (post one GRPO per branch).
- **Currency and rate:** for a foreign-currency PO, the GRPO takes the PO's currency and `DocRate`. TZS POs are
  unchanged. If the lines come from POs with different currencies or rates → 400 naming the POs (send one GRPO
  per PO).
- **Bins (warehouse 003 and other bin warehouses):** send `bin_abs` per line, or leave it out and the
  middleware picks:
  - the item's default bin, or the single bin that holds it → used automatically;
  - the item has stock in **several** bins → 400 `destination bin required … (resolver: options)`. Send
    `bin_abs` (choose via `GET /bins?item_code=&whs_code=&direction=destination`);
  - the item has **never been in a bin** of that warehouse → **003 only:** received into
    `003-SYSTEM-BIN-LOCATION` (bin_abs 1), to be put away later. Other bin warehouses: 400, send `bin_abs`.
- **Response:** `data.doc_entry` and `data.doc_num`, also on a replay (`already_existed: true`).
- **Idempotency:** unchanged. A retry with the same `app_ref` returns the original GRPO. A request refused
  with 400/409/422 posts nothing, so the same `app_ref` can be reused after fixing it.

## 2. `GET /purchase-orders`

Each open line now also has:

- `supplier_cat_num`: `POR1.VendorNum`;
- `doc_currency` and `doc_rate`: the PO's `DocCur` and `DocRate`.

**Important: do not match the packing list's "TAN No." against `supplier_cat_num`.** On the live POs,
`POR1.VendorNum` holds the **brand** ("VIKA", "BORSEHUNG"), not the supplier's article number. That explains
the ~90% fill rate. Match on **`article_number`** (`OITM.U_Article_No`) as today. For PO 235 it holds the
Germax numbers (GL0911, GL0912, …).

## 3. `POST /purchase-invoice-drafts` (new)

Request:

```json
{
  "app_ref": "shipment-12-invoice",
  "card_code": "VEN1011",
  "num_at_card": "GAPC130726",
  "doc_date": "2026-11-16",
  "tax_date": "2026-07-13",
  "comments": "Receive Shipment #12: GRPO 2552, 2553",
  "grpo_doc_entries": [2552, 2553],
  "expected_total": null
}
```

- `app_ref` (required, max 40) → `ODRF.U_AppRef`.
- `card_code` (required).
- `num_at_card` (required, supplier invoice number, cut to 100).
- `doc_date` → `DocDate` (posting date). Today when omitted.
- `tax_date` → `TaxDate` (supplier invoice date). SAP's default when omitted.
- `DocDueDate` is not sent: SAP derives it from the supplier's payment terms.
- Repeated GRPO numbers are counted once.

Each draft line copies an open GRPO line (`BaseType 20`) for its open quantity. Price, tax, warehouse,
currency, rate and branch come from the GRPOs. The draft is never added as an invoice: finance does that in
SAP.

Answers:

| Status | When |
|---|---|
| 200 | `data`: `draft_doc_entry`, `draft_doc_num`, `line_count`, `doc_total`, `doc_currency`, `already_existed` |
| 200, `already_existed: true` | a draft with this `app_ref` exists (replay) |
| 400 | a GRPO does not exist, belongs to another supplier, is cancelled, or is closed (nothing left to invoice); or the GRPOs differ in currency, rate or branch. Every offending GRPO number is named; nothing is created |
| 409 | the supplier already has a non-cancelled A/P invoice or an open A/P invoice draft with this `num_at_card` under another `app_ref`. Nothing is created |
| 422 | `expected_total` given and SAP's total differs by more than 0.01. The draft is deleted; the message has both totals |
| 500 | SAP refused the draft (message from SAP) |

Use **`draft_doc_entry`** for the status route below. `draft_doc_num` is the number finance sees in SAP's
drafts list.

## 4. `GET /purchase-invoice-drafts/{draftDocEntry}` (the optional status route)

```json
{
  "draft_doc_entry": 222, "status": "added", "draft_doc_num": 61,
  "card_code": "VEN1011", "num_at_card": "GAPC130726", "app_ref": "test-po235-invoice",
  "doc_total": 7546500, "doc_currency": "TZS",
  "invoice_doc_entry": 61, "invoice_doc_num": 61, "invoice_cancelled": false
}
```

`status`:

- `open`: waiting for finance.
- `added`: finance added it. `invoice_doc_entry` and `invoice_doc_num` are the A/P invoice, found through
  `OPCH.draftKey`; `invoice_cancelled` is true if that invoice was later cancelled.
- `closed`: closed in SAP without an invoice.
- `deleted`: the draft no longer exists.

## Errors

The format is the same for every route:
`{ "success": false, "data": null, "meta": null, "errors": ["…", "…"] }`. Show `errors` to the user; each
message names the line, PO or GRPO concerned.

## Related: POs created from Autohub invoices

Reviewed supplier invoices on the middleware (`/autohub/documents/{id}` → Create & finish → Purchase order)
now create the SAP PO directly:

- in TZS at PL01, every line in warehouse **003**;
- `NumAtCard` = the supplier invoice number, `TaxDate` = the invoice date;
- each line carries our ItemCode.

These POs show up in `GET /purchase-orders` like any other and can be received with `POST /grpo`. PO 235 above
was created this way.
