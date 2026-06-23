# DGX (spark-09cc) — Role, Components & Contract

> ⚠️ **LIVING DOCUMENT — keep this in sync.** This is the canonical description of the DGX's role and
> its contract with the middleware. **Whenever the DGX side changes** — a new/changed endpoint, a
> changed request/response field, the brand→SAP-hint map, the OEM filter, the routing paths, or once
> the `_tecdoc_fetch_full` / `_oem_bridge_candidates` / `_upsert_oitm` helpers are wired — **update
> this file in the same change.** It is the first thing to read when picking up DGX work in a new
> session. Anchor it to the real artifacts in `deploy/dgx/` and the middleware enrichment DTOs.

> Seed document for a fresh Claude chat continuing the **DGX Spark** setup for the
> SAP ↔ Odoo middleware (Molas Autohub / Lubes). Everything here is reconstructed from the
> actual artifacts in the `sap-odoo-middleware` repo (`deploy/dgx/*`) and the middleware's
> enrichment client/DTOs — it is the real contract, not a sketch.

---

## 1. What the DGX is, in one paragraph

The **DGX** (an NVIDIA DGX/Spark box, hostname **`spark-09cc`**, reachable over **Tailscale** at
**`http://spark-09cc:8077`**) is the **AI/GPU service** behind the middleware. It runs a single
FastAPI app — **`classifier_service.py`** (in `~/Inventory_Management_Tool/`) — managed as the
systemd service **`inventory-classifier`**. It does two jobs:

1. **Vision extraction** — turn a rendered invoice page (image) into structured JSON line items,
   using a local **Ollama** vision model.
2. **Parts enrichment** — for each spare-parts line, look the part up in **TecDoc (via RapidAPI)**
   and the **`parts_catalog` (Neon)** mirror, decide a match strategy, and return a complete
   enrichment package (SAP item group, SKU prefix, OEM cross-refs, donor id) that the middleware
   turns into a real SAP B1 item.

The middleware **never** talks to TecDoc, Ollama, or the GPU directly. It only makes HTTP calls to
the DGX endpoints. The DGX is the system's "brain"; the middleware is the orchestrator and the
SAP/Neon writer.

---

## 2. Where the DGX sits (data flow)

```
                 ┌─────────────────────────── MIDDLEWARE (.NET 8, Windows) ───────────────────────────┐
 invoice PDF ──► │ render page → image                                                                │
                 │      │                                                                              │
                 │      ▼   POST /extract_parts_invoice (image_base64)                                 │
                 │   ┌──────────────┐  JSON lines                                                      │
                 │   │     DGX      │◄──────────────  (Ollama vision model, temp=0, format=json)       │
                 │   │  spark-09cc  │                                                                   │
                 │   │   :8077      │                                                                   │
                 │   └──────────────┘                                                                  │
                 │      ▲                                                                               │
                 │      │   POST /enrich_item (article, oems, brand, description)                       │
                 │      │                                                                               │
                 │   per create_new line ───────────────► DGX enrich:                                  │
                 │                                          Path A TecDoc-direct  ─┐                    │
                 │                                          Path C OEM-bridge borrow├─► parts_catalog   │
                 │                                          partial / unmatched   ─┘   .oitm (Neon)     │
                 │      ▲                                                                               │
                 │      │ EnrichmentResponse {item_data, neon_oitm_id, source, confirmation_required}   │
                 │      ▼                                                                               │
                 │  PartsItemProvisioningService → SAP B1 OITM  +  NeonBridge stamps item_code on oitm  │
                 └────────────────────────────────────────────────────────────────────────────────────┘

External deps the DGX owns:  Ollama (local vision LLM) · RapidAPI TecDoc · Neon parts_catalog DB
Network:  Tailnet only (Q7 — no bearer token; Tailscale ACL is the auth boundary)
```

---

## 3. Host, service & runtime

| Thing | Value |
|---|---|
| Host / Tailscale name | **`spark-09cc`** |
| Base URL (Tailnet) | **`http://spark-09cc:8077`** |
| App file | **`~/Inventory_Management_Tool/classifier_service.py`** (FastAPI) |
| Service manager | **`sudo systemctl restart inventory-classifier`** |
| Vision backend | **Ollama** at `OLLAMA` (`/api/chat`), `VISION_MODEL`, `temperature=0`, `format=json` |
| Auth | **Tailscale-only** (no token in Slice 1) |
| Middleware config key | `Companies:Autohub:Classifier:BaseUrl` (resolved per-tenant via `ICompanyContext`) |

The middleware's typed HTTP client (`HttpEnrichmentClient`) builds the URL as
`{Classifier.BaseUrl}/enrich_item` and is registered with a **long timeout** (enrichment can trigger
a cold-item lookup/scrape).

---

## 4. Endpoints exposed by the DGX

`classifier_service.py` is patched **idempotently** by the two scripts in `deploy/dgx/`. Endpoints:

### Phase A — extraction (already in use)
- **`POST /extract_invoice`** — Lubes (lubricants) invoice extraction. **Pre-existing; left untouched.**
- **`POST /extract_parts_invoice`** — Autohub spare-parts extraction. Added by
  `extract_parts_invoice_patch.py`. Same Ollama model / `num_ctx=16384`, JSON-only. Body:
  `{ image_base64, page_no }` → `{ header{…}, lines[{ supplier_article_number, oem_numbers[], description, brand, quantity, unit, unit_price_foreign, discount_pct, line_total_foreign }] }`.
  The prompt (`PARTS_INVOICE_PROMPT`) handles multi-currency (USD/AED/GBP/EUR), US/EU number &
  date formats, OEM separators (`/`, `+`), promotional `$0.00` lines, multi-page (total only on last
  page), and per-line brand variation.

### Phase B — enrichment (Slice 1; routing live, data helpers to wire)
Added by `enrich_item_patch.py`:
- **`POST /enrich_item`** — the main one. Routes a line to a match strategy and returns the package.
- **`POST /lookup_tecdoc_oem_bridge`** — debug/helper: OEM → donor candidates from `oitm_cross_reference`.
- **`POST /fetch_tecdoc_full`** — debug/helper: full TecDoc record by article / tecdoc id / supplier.
- **`POST /verify_image_visu`** — HEAD-only image reachability check (**implemented**, no wiring needed).

---

## 5. `/enrich_item` — the contract (matches the middleware DTOs exactly)

### Request (middleware → DGX)
The middleware sends a **flat** shape (DGX also tolerates a nested `{extracted:{…}}` shape):
```json
{
  "supplier_article_number": "06J109259A",
  "oem_numbers": ["06J109259A", "06L109259A"],
  "brand": "VAG",
  "description": "Timing chain tensioner",
  "vehicle_category_hint": null
}
```
> Before sending, the middleware runs its **Option-C OEM filter** (`OemFilterService`) to strip
> position/engine noise (FRONT/REAR/LEFT, `2.0L`, etc.). The DGX re-runs the same filter
> (`_ah_clean_oems`) belt-and-suspenders.

### Response (DGX → middleware) — key fields the middleware reads
| JSON key | Meaning |
|---|---|
| `status` | `success` \| `partial` \| `failed` (omitted ⇒ treated as success) |
| `source` / `enrichment_source` | strategy label: `tecdoc_direct`, `borrowed_oem_bridge`, `unmatched` |
| `confirmation_required` | DGX's blanket flag (**note below**) |
| `neon_oitm_id` | `parts_catalog.oitm.id` of the pre-enriched row — **the donor id NeonBridge links to** |
| `borrowed_from` | `{article_number, supplier_id, supplier_name, match_via_oem, match_confidence}` |
| `item_data` | the SAP-build block (see below) |
| `error` | `{code, message, retryable}` on `failed` |
| `noise_filtered_tokens` | OEM tokens the filter dropped |

`item_data` (→ `EnrichmentItemData`):
```
primary_description, frgn_name, fit_for_auto, image_url, all_image_urls, product_url,
tecdoc_categories[], compatible_vehicles[], filtered_oems[],
suggested_itms_grp_cod (int → SAP OITB group), suggested_sku_prefix (string)
```

> **Important nuance the middleware encodes:** DGX sets `confirmation_required=true` on almost every
> borrowed/unmatched line, so the middleware does **NOT** use it as the create gate. The real gate is
> `MatchStrategy` → `EnrichmentStrategies.IsCrossSupplierStrategy(...)`: only a genuine cross-supplier
> borrow needs operator sign-off; same-supplier / own-data creates straight through.

---

## 6. `/enrich_item` routing logic (what the DGX decides)

1. **Path A — TecDoc direct** (`source=tecdoc_direct`, `confirmation_required=false`).
   If the article "looks like" a TecDoc code (`_ah_looks_like_tecdoc_article`: ≥4 chars, has a digit
   and a letter) → `_tecdoc_fetch_full(article_number=…)`. On a hit, upsert the `oitm` row and return
   success. Auto-confirmed.
2. **Path C — borrowed via OEM bridge** (`source=borrowed_oem_bridge`, `confirmation_required=true`).
   For each clean OEM (up to `max_oem_bridges_to_try`, default 5), `_oem_bridge_candidates(oem)` walks
   `oitm_cross_reference` (`reference_type='oem'`). **First hit wins (Q10).** Fetch the donor's full
   TecDoc record, mint an `oitm` row tagged `borrowed_oem_bridge`, return success **with** `borrowed_from`.
3. **Path B — Germax scrape** — **DEFERRED to Slice 2** (no `/lookup_germax`, `/scrape_germax` yet).
4. **Partial / unmatched** (`status=partial`, `source=unmatched`, `item_data=null`). Nothing matched →
   middleware routes the line to **needs_manual** (operator does a manual create with item group + prefix).
5. **Failed** — `NotImplementedError` (helper not wired) ⇒ HTTP **501** `integration_not_wired`;
   any other exception ⇒ HTTP 200 with `status=failed` so the middleware soft-routes to needs_manual.

Idempotency: `request_id` is cached in-process for **5 minutes** (`_AH_CACHE`, `_AH_CACHE_TTL=300`).

---

## 7. The FOUR integration helpers that must be wired (current main gap)

The patch is a **scaffold**: orchestration + response contract are complete, but four data-access
functions raise `NotImplementedError` until pointed at the existing DGX TecDoc/DB code. Each documents
the exact dict shape it must return, so wiring is mechanical:

| Helper | Must do | Returns |
|---|---|---|
| `_tecdoc_fetch_full(tecdoc_article_id?, article_number?, supplier_id?)` | Call the **RapidAPI TecDoc** client (the "VAG9999" flow) | full record dict (`article_number, supplier_name, supplier_id, tecdoc_article_id, description, fit_for_auto, image_url, all_image_urls[], all_oems[], product_url, frgn_name, tecdoc_categories[], compatible_vehicles[], specs{}`) or `{found:false}` |
| `_oem_bridge_candidates(oem_number, brand_hint?, max_results)` | Walk `oitm_cross_reference` (`reference_type='oem'`), rank OEM>Mann>Mahle>Vaico>… | ranked list `{donor_tecdoc_article_id, donor_article_number, donor_supplier_name, donor_supplier_id, matched_via_oem, confidence}` or `[]` |
| `_upsert_oitm(td, brand, oems, source, borrowed?)` | Create-or-locate `parts_catalog.oitm` (item_code **NULL** until NeonBridge sets it) + `oitm_cross_reference` rows; idempotent on (article_number, supplier_name, source) | integer `neon_oitm_id` |
| `_verify_image(url)` | **Already implemented** (HEAD-only). Swap in a real vision check if desired (Q5: off in Slice 1) | `{ok, url_reachable, mime_type, size_bytes, …}` |

Until `_tecdoc_fetch_full` / `_oem_bridge_candidates` / `_upsert_oitm` are wired, Paths A & C return
**501 integration_not_wired** — which itself confirms the routing/contract is live.

> **TODO(mohamed) flagged in the code:** confirm the `itms_grp_cod` values in `_AH_BRAND_HINTS`
> against the real SAP **OITB** groups (they are best-guess), and point the two helpers at the real
> RapidAPI client + `oitm_cross_reference` mirror once the module names are visible on the box.

---

## 8. SAP hints & OEM noise filter (data the DGX derives locally)

**Brand → SAP hints** (`_AH_BRAND_HINTS`, `_ah_sap_hints`): maps brand (or an `LR…` OEM prefix) to
`(suggested_sku_prefix, suggested_itms_grp_cod, vehicle_category)`. Examples: VAG/AUDI/VW/SKODA/SEAT →
`("VAG",105)`; BMW/MINI → `("BM"/"MINI",105)`; MB/Mercedes → `("MB",106)`; FORD → `("FRD",107)`;
**LR / Land Rover / GERMAX → `("LR",108)`**. Fallback `("GEN",105,"VAG")`.

**OEM noise filter** (`_ah_clean_oems`, mirrors `OemFilterService`): drops position/engine tokens
(`_AH_NOISE`: FRONT/REAR/LEFT/RIGHT/UPPER/LOWER/PETROL/DIESEL/V6/V8…), drops `^\d+(\.\d+)?L$` engine
sizes, keeps tokens matching `^[A-Z0-9][A-Z0-9\-\s]{3,19}$` that contain at least one digit.

---

## 9. Files in the repo that define/deploy the DGX side

| Path | What it is |
|---|---|
| `deploy/dgx/extract_parts_invoice_patch.py` | Idempotent patch adding **`/extract_parts_invoice`** (Phase A vision prompt) to `classifier_service.py`. |
| `deploy/dgx/enrich_item_patch.py` | Idempotent patch adding **`/enrich_item`** + 3 helpers (Phase B Slice 1). Contains the routing, response builders, brand map, OEM filter, and the 4 stubs to wire. |
| `deploy/dgx/test_enrich_item.sh` | Smoke test over the Tailnet: Path A, Path C, partial, and `/verify_image_visu`. `DGX=http://localhost:8077 ./test_enrich_item.sh`. |

**Middleware side that calls the DGX** (for reference, do not change to set up DGX):
`Services/Autohub/EnrichmentService.cs` (request/response DTOs + Option-C filter),
`Services/Autohub/HttpEnrichmentClient.cs` (typed HttpClient → `/enrich_item`),
`Services/Autohub/EnrichmentResultRouter.cs` (maps the response to a match strategy / review status).

---

## 10. Tools & dependencies the DGX uses

- **Ollama** (local vision LLM) — invoice→JSON extraction (`/api/chat`, `format=json`, `temp=0`).
- **RapidAPI TecDoc** — part master data, OEMs, images, compatible vehicles (the "VAG9999" flow).
- **Neon `parts_catalog` Postgres** — `oitm` (match mirror) + `oitm_cross_reference` (OEM bridge).
  The DGX writes `oitm` rows with `item_code` NULL; the middleware's **NeonBridge** later stamps the
  real SAP item code onto that row so future invoices auto-match.
- **Tailscale** — the only network boundary (no app-level auth in Slice 1).
- **FastAPI / Uvicorn** under systemd (`inventory-classifier`).

---

## 11. Setup / deploy checklist (run ON spark-09cc)

```bash
# 1. Apply the patches (idempotent — safe to re-run)
cd <repo>/deploy/dgx
python3 extract_parts_invoice_patch.py        # adds /extract_parts_invoice
python3 enrich_item_patch.py                   # adds /enrich_item + helpers

# 2. Wire the three data helpers in ~/Inventory_Management_Tool/classifier_service.py:
#    _tecdoc_fetch_full, _oem_bridge_candidates, _upsert_oitm
#    (point at the existing RapidAPI TecDoc client + parts_catalog DB code)

# 3. Confirm the itms_grp_cod values in _AH_BRAND_HINTS against SAP OITB groups

# 4. Restart and smoke-test
sudo systemctl restart inventory-classifier
./test_enrich_item.sh                          # Path A & C show 501 until step 2 is done; partial returns 200
```

---

## 12. Known status & open items (as of this handoff)

- **Extraction** (`/extract_parts_invoice`) — working; in production use for Autohub invoices.
- **Enrichment** (`/enrich_item`) — routing/contract live; the three data helpers
  (`_tecdoc_fetch_full`, `_oem_bridge_candidates`, `_upsert_oitm`) **need wiring** to leave 501.
- **Multi-OEM ItemName enhancement (middleware ask of DGX):** today `filtered_oems` / the line OEMs
  carry ~1 OEM per part, so created SAP items show a single OEM. To populate the full OE supersession
  chain in the SAP `ItemName` (`OEM1/OEM2/…/article`), **`_tecdoc_fetch_full` must return the complete
  `all_oems` list and `_upsert_oitm` must persist all of them as `reference_type='oem'`
  cross-references.** This is a DGX-side data change, not a middleware change.
- **Germax scrape (Path B)** — deferred to Slice 2.
- **Image vision verification** — off (Q5); HEAD-only check in place.
```
```
```

---

### One-line summary to paste into the new chat
> *"The DGX (`spark-09cc:8077`, systemd `inventory-classifier`, FastAPI `classifier_service.py`) is the
> middleware's AI service: Ollama-based invoice→JSON extraction (`/extract_parts_invoice`) and
> TecDoc/OEM-bridge parts enrichment (`/enrich_item`), writing `oitm` rows in Neon `parts_catalog`.
> Slice-1 routing is live; the three data helpers `_tecdoc_fetch_full`, `_oem_bridge_candidates`,
> `_upsert_oitm` still need wiring to the real RapidAPI TecDoc client and DB. Patches live in
> `deploy/dgx/`."*
