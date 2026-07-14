# Handoff — DGX: `POST /materialize_candidate` (for "Find more candidates" swap)

**From the middleware side → for the DGX.** We chose **Option 2**: when the operator swaps the borrow
to a candidate from the broad `/candidates_for_line` pool (a TecDoc/RapidAPI article that is **not** in
`parts_catalog`), the **DGX materializes it into `oitm`** and returns a `neon_oitm_id`. The middleware
then re-points the line's borrow to that id using the swap mechanic already built — no middleware writes
to `parts_catalog`, keeping that boundary on the DGX where it belongs.

This doc specifies the one new endpoint we need, and confirms the two already-live endpoints we'll code
the "Find more" flow against.

---

## New endpoint required: `POST /materialize_candidate`

**Purpose:** upsert a single chosen TecDoc article into `parts_catalog.oitm` (with its OEM cross-refs and
detail fields), `item_code = NULL`, and return its `neon_oitm_id`. Idempotent.

### Request
```json
{
  "tecdoc_article_id": 71428,          // preferred key when present
  "article_number": "0 986 494 818",   // fallback identity
  "supplier": "BOSCH",
  "supplier_id": 30,                    // optional (image path / dedup)
  "description": "Brake Pads",          // optional context (part-type verdict)
  "line_oems": ["LR128263"]            // optional context (the line's OEMs)
}
```

### Action (mirror the existing `_upsert_oitm` / enrichment write)
- Resolve the article via the RapidAPI/TecDoc client (same path `/candidates_for_line` already uses).
- **Upsert** an `oitm` row: `item_code = NULL`, `source = 'rapidapi_materialized'` (or similar), populate
  `name`, `image_url`, `part_component`, `is_kit`, `specs_json`/`spec_count`,
  `compatible_vehicles_count`, `categories_count`, `canonical_oem_number`, and the `oitm_cross_reference`
  rows (`reference_type='oem'` for genuine OEMs).
- **Idempotent** on `(tecdoc_article_id)` (or `(article_number, supplier)` when id absent): if already
  materialized, return the existing row's id — do **not** duplicate.
- **Never write an `item_code`.** This is a donor row; the middleware mints the SAP SKU at creation and,
  because it's a different article, routes it to create-new **own-identity** (Part A gating), so the
  donor row is never overwritten.
- Write to the **Autohub `Parts_Catalog`** Neon DB (same target the deep worker / `_upsert_oitm` use).

### Response (success)
```json
{
  "found": true,
  "neon_oitm_id": 12345,               // ← the ONLY field the swap strictly needs
  "article_number": "0 986 494 818",
  "supplier": "BOSCH",
  "name": "Brake Pad Set, disc brake",
  "image_url": "https://.../....webp",
  "part_component": "Brake Pad Set",
  "is_kit": false,
  "spec_count": 6,
  "compatible_vehicles_count": 128,
  "categories_count": 2,
  "oem_numbers": ["C2C4198","LR061373","LR128263","LR160435","LR160486"],
  "oem_count": 6,
  "crossref_count": null               // TecDoc article data lacks aftermarket count → null
}
```
Returning the detail (not just the id) lets the modal update the picked card in place without a re-fetch.

### Errors (build null/error-tolerant on the middleware)
- RapidAPI disabled → HTTP **501** `{"error":"rapidapi_disabled"}`.
- Article not resolvable → `{"found": false}` (HTTP 200).
- Any other failure → non-2xx; the middleware keeps the local list and surfaces "couldn't add that donor".

---

## How the middleware uses it (so the contract is unambiguous)
1. Modal opens → shows the **local** `bridge_candidates_ranked` (instant, from the payload).
2. Operator clicks **"Find more candidates"** → middleware proxies `POST /candidates_for_line`
   `{ oem_numbers, description }` → merges + dedupes (by `tecdoc_article_id`) with the local list.
3. Operator expands an API candidate's OEMs → middleware proxies `POST /article_oems { tecdoc_article_id }`.
4. Operator picks an **API** candidate and confirms → middleware calls **`POST /materialize_candidate`** →
   gets `neon_oitm_id` → re-points `staging_document_line.NeonOitmId` to it → re-runs the router → done.
   (Picking a **local** candidate keeps the existing local re-point; no materialize call.)

At SAP creation the ItemName reads the materialized row's OEM cross-refs via `GetOemCrossReferencesAsync`,
so the borrowed OEM chain flows through automatically. The new item still gets a fresh minted SKU.

---

## Two endpoints already live — confirming the shapes we'll code to
(from `MIDDLEWARE_handoff_find_more_candidates.md` and `MIDDLEWARE_handoff_oem_crossref_split.md`)

- `POST /candidates_for_line` `{ oem_numbers, description, max_per_oem?, top_n? }` → scored pool with
  `{ name, supplier, article_number, tecdoc_article_id, supplier_id, verdict, score, matched_oem,
     crossref_count(null), image_url, is_default }`.
- `POST /article_oems` `{ tecdoc_article_id }` → `{ found, oem_numbers[], oem_count, crossref_count(null) }`.

If any of these field names/paths differ in the live build, tell me and I'll match them.

---

## Notes
- **On-demand only** — the middleware calls `/candidates_for_line` and `/materialize_candidate` on the
  operator's click, never on modal open.
- **Additive** — none of this touches `/enrich_item`; existing enrichment/selection is unchanged.
- Middleware work (proxy endpoints + swap-to-API + "Find more" UI) is built on top of the modal
  redesign PR and lands once this endpoint is confirmed live.
