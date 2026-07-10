# ADR: Deep `/enrich_item` Enrichment — Architecture Decision

> Decision record for making DGX `/enrich_item` enrichment "deep" (full OE supersession chains +
> compatible vehicles + categories + kit detection) instead of the current shallow result.
> Grounded in the actual middleware code (`Services/Autohub/*`, `Controllers/AutohubDocumentsController.cs`,
> the enrichment/auto-match workers) as of 2026-06. Companion to `deploy/dgx/DGX_ROLE.md`.
>
> **Status: Recommended (not yet built).** Recommendation = **Option B (background deep pass)** with a
> targeted inline-deep exception on the single-line on-demand endpoint.

---

## Context

On the DGX, `classifier_service.py`'s `_tecdoc_fetch_full` is currently **local-only**: it reads
`parts_catalog.oitm` from Neon and returns `compatible_vehicles: []` / `tecdoc_categories: []` hardcoded
empty, with `all_oems` limited to whatever cross-refs already exist on that row. No live RapidAPI/TecDoc
call. Lines whose parts aren't already richly enriched come back **thin**.

A proven **deep pipeline** exists (tokenize → classify → kit-detect → TecDoc verify/rediscover → enrich
5 Neon tables) but runs as **offline batch scripts**, not in the live path. Goal: make enrichment deep.

Two candidate architectures:

- **(A) Live deep enrichment** — `/enrich_item` does full RapidAPI + deep logic inline per call.
  Accurate, but slower per item and adds API-call volume to the live path.
- **(B) Background deep pass** — `/enrich_item` stays fast/local; a separate worker deep-enriches `oitm`
  rows (ahead of time or queued), so the live local read returns rich data once the row is enriched.

---

## Findings from the middleware code

### 1. When `/enrich_item` is called

Three call sites:

| Caller | Trigger | Concurrency | Blocks a user? |
|---|---|---|---|
| **`EnrichmentBackgroundWorker`** (primary) | `BackgroundService`, `PeriodicTimer` every `PollIntervalSeconds` (default **30s**), `BatchSize` lines/pass (default **20**), `Task.Delay(200)` between lines | **Sequential, one line at a time** | No — background |
| **`AutohubDocumentsController` `…/lines/{id}/enrich`** | Operator action (borrowed-data modal / "Create New" on a cache miss) | One line | **Yes** — synchronous to the HTTP request |
| **`PartsItemProvisioningService.ProvisionAsync`** | Only re-calls DGX **if the line has no stored `EnrichmentPayloadJson`** | One line, inside Bulk Create | Indirectly (during create) |

Dominant path is the background worker. **Creation normally does NOT re-call DGX** — `ProvisionAsync`
deserializes the stored payload; the DGX call happens once, at enrichment time.

### 2. Real timeout & tolerance

`HttpEnrichmentClient.Timeout` = `VisionExtractorSettings.TimeoutSeconds` = **600s** (per current config).
A 5–30s deep call is far inside the per-call ceiling — **it will not trip the timeout**.

There is **no per-invoice time budget**. The constraint is **throughput**, not correctness:
- The worker is **strictly sequential** (one line + 200ms delay). At ~30s/item a **500-line invoice ≈ 4+
  hours**, tying up the single worker; a slow/hung line can stall a pass up to 600s.
- On-demand single-line at 5–30s is fine.

### 3. Already async/queued?

**Yes — the middleware already IS the queue.** DB-backed enrich → persist → poll: the worker pulls
`GetLinesNeedingEnrichmentAsync`, persists via `RecordEnrichmentResultAsync`, and the **review UI reads
persisted state without re-calling DGX**. Only the on-demand endpoint blocks on a live call. Same shape as
the OdooSyncQueue polling model — **the middleware is already built for (B)**.

### 4. What the middleware consumes from `EnrichmentResponse`

**Router (`EnrichmentResultRouter`)** reads only: `SourceLabel`, `Status`, `ItemData` (null-check only),
`NeonOitmId`, `BorrowedFrom.{ArticleNumber,SupplierName}`, `ConfirmationRequired`, `Error.Code`. It
**serializes the whole response** into `EnrichmentPayloadJson`. Routing is driven by `neon_oitm_id` +
supplier identity — **not** the OE list / fitment / categories.

**Creation (`ProvisionAsync`)** reads `ItemData.SuggestedItmsGrpCod`, `SuggestedSkuPrefix`,
`PrimaryDescription`, `NeonOitmId`. For the **OE chain it does NOT read the response** — it calls
`_bridge.GetOemCrossReferencesAsync(neonOitmId)`, i.e. **queries `oitm_cross_reference` in Neon**.

Consequences:
- **Deep OE data does NOT need to flow through the response.** If the DGX enriches the `oitm` row in Neon
  (more `reference_type='oem'` cross-refs), the middleware **automatically benefits** at creation. No
  response-schema change needed for the OEM/ItemName ask.
- **`compatible_vehicles` / `tecdoc_categories`**: response fields exist (`EnrichmentItemData`), but the
  middleware **consumes neither** — `CreateAutohubItemAsync` never maps them to SAP. So fitment/categories
  do **nothing in SAP today** until explicit middleware mapping (e.g. to SAP UDFs) is added. Separate,
  additive change regardless of A/B.

### 5. Where ItemName is built

`PartsItemProvisioningService.BuildItemName(oems, article)` → "OEM1/OEM2/…/article" (max 5 OEMs + article,
200-char cap), fed by `MergeOems(filtered line OEMs, donorOems)` where
`donorOems = GetOemCrossReferencesAsync(enr.NeonOitmId)` — **read from `oitm_cross_reference` in Neon at
creation time, not from the response.**

**So the multi-OEM ItemName is sourced from Neon, decoupled from the response.** Deep OE data only has to
**land in `oitm_cross_reference` before the item is created.**

---

## Decision: Option **B** (background deep pass), with a targeted inline-deep exception

**(B) fits the middleware as-built; (A) fights it.**

1. The ItemName (headline ask) reads OE data from Neon, not the response (finding 5) — a background worker
   that deep-enriches the `oitm` row feeds the ItemName automatically. (A) buys nothing extra here.
2. Enrichment is already a background, DB-polled, persist-then-read queue (finding 3). Deep-in-Neon is the
   natural extension; "one synchronous deep answer" is not how the live path works.
3. Routing decisions don't need deep data (finding 4) — they run off `neon_oitm_id` + supplier identity.
   Deep enrichment is purely additive content; it can't regress routing → (B) is low-risk.
4. (A) makes every call slow, including the ones a human waits on (background crawl on big invoices, the
   "Create New" modal spinning 5–30s, `ProvisionAsync` cache-miss re-call slowing Bulk Create). (B) keeps
   `/enrich_item` fast/local so all live paths stay snappy.

### Shape to build
- Keep `/enrich_item` fast and local (returns whatever's in the row now).
- A **separate deep-enrichment worker** (clone the `EnrichmentBackgroundWorker` template: `PeriodicTimer`,
  Autohub-pinned scope, batch, persist) walks `oitm` rows / distinct articles and writes the 5 Neon tables
  — OE chain → `oitm_cross_reference`, fitment/categories → theirs. **Seed it at extraction time** (enqueue
  each distinct article) so rows are usually deep by review/create time.
- **Inline-deep exception:** on the **on-demand single-line** `…/enrich` endpoint, do the deep call inline.
  N=1, operator already waiting; guarantees rich data the moment they explicitly ask, without slowing the
  bulk path. Hybrid: B for bulk/background, A for the single deliberate click.

### Race to plan for
If an operator creates an item **before** the deep pass covers that article, the ItemName builds from the
shallow row. Mitigate with extraction-time seeding and/or the inline deep call on the on-demand path.
**Do NOT hard-gate Bulk Create on "all rows deep"** — that reintroduces the throughput coupling (B) avoids.

---

## Risks to the live Autohub pipeline

- **Throughput collapse under (A):** single sequential worker + 200ms/line → hours for large invoices; a
  600s-timeout deep call stalls the whole pass on one line. Isolating deep work in its own worker (B)
  contains this.
- **DGX idempotency cache won't help:** the 5-min cache is keyed on `request_id`, but `HttpEnrichmentClient`
  sends the flat shape **with no `request_id`** → DGX mints a random one per call → **no dedupe across
  passes/retries**. Under (A) every re-pick re-runs the full RapidAPI deep pipeline (volume / rate-limit /
  cost risk). Under (B), the deep worker must **dedupe by article in its own queue** — don't rely on the
  DGX cache.
- **RapidAPI volume** scales with line count either way; (B) lets you rate-limit/seed independently of
  operator activity.

---

## Tenancy gotcha

- **SAP side is safe.** The handoff caveat (`SapB1DiApiService` uses the top-level **Lubes** connection
  regardless of tenant) does **not** affect this path: `PartsItemProvisioningService` injects
  `IAutohubSapB1Service` → `AutohubSapB1DiApiService` (constructed from `Companies:Autohub:SapB1` →
  `MOLAS_Live_2021`). Enrichment writes Neon, not SAP. No SAP tenant-mismatch. ✅
- **Neon side is the real gotcha for (B).** Two Neon DBs: Lubes `MolasLUBES` (top-level `Neon`) and Autohub
  **`Parts_Catalog`** (`Companies:Autohub:Neon`). Existing workers pin the tenant with
  `CompanyContext.SetCompany(AutohubKey)` so `NeonBridgeService`/repos resolve `Parts_Catalog`. **The new
  deep worker MUST do the same** — forgetting `SetCompany(AutohubKey)` makes it read/write the **wrong Neon
  DB** (Lubes), silently corrupting/missing the parts catalog.
- The DGX's own `_upsert_oitm` / `_tecdoc_fetch_full` write to `parts_catalog` **directly, bypassing
  middleware tenancy** → the DGX must be pointed at the Autohub `Parts_Catalog` Neon independently.
- Keep deep enrichment **SAP-free** (Neon-only) and the Lubes-default `ISapB1Service` caveat never arises.

---

## Bottom line

Build **(B)**. The middleware already polls-and-persists, routing ignores deep content, and the ItemName
reads OEMs from `oitm_cross_reference` in Neon — so deep data just needs to **land in Neon before
creation**, which a background deep worker (seeded at extraction) delivers without slowing any live path.
Add the inline deep call only on the single-line on-demand endpoint. Watch the `SetCompany(AutohubKey)`
Neon-tenant pin and the missing-`request_id` dedupe gap.

### Follow-on work this implies (additive, separate from A/B)
- Map `compatible_vehicles` / `tecdoc_categories` to SAP UDFs if they should appear on the item (today the
  middleware consumes neither).
- Add a stable `request_id` to `HttpEnrichmentClient` requests if you want the DGX idempotency cache to
  dedupe across passes/retries.
- Build the deep-worker queue with per-article dedupe; seed it at extraction time.

---

## Follow-up decision: worker location — DGX-side Python (resolved 2026-06-25)

The ADR described the deep worker as a middleware (.NET) EnrichmentBackgroundWorker clone but left the implementation location open. Decision: build the deep worker as a DGX-side Python service, not a .NET middleware worker.

### Rationale
1. The deep logic already exists on the DGX and is proven — the pass_1_2→pass_3 enrichment modules and the invoice re-enrichment adapters ran successfully against the Autohub Parts_Catalog Neon DB. A .NET worker cannot run this logic; it would have to call a new DGX endpoint anyway. DGX-Python wraps existing, tested code in a timer.
2. Zero middleware changes. Creation already reads OEMs from oitm_cross_reference in Neon via GetOemCrossReferencesAsync(neonOitmId), not from the enrichment response. A DGX worker that deepens those rows in place means the middleware benefits automatically — no .NET code, no Windows rebuild, no PR, no redeploy. Smaller blast radius on production.
3. Dissolves two of the three risks in this ADR. The missing-request_id dedupe gap and the HttpEnrichmentClient flat-shape problem are properties of middleware→DGX HTTP calls. A DGX-side worker reads its own queue and writes Neon directly — it makes no such calls, so those risks don't apply. It dedupes by article in its own SQL state.
4. No cross-system seeding needed. Instead of the middleware enqueueing articles at extraction time, the DGX worker polls Neon for not-yet-deep rows itself, which removes a cross-system coupling.

### Risk that remains (must handle explicitly)
- Neon tenant targeting. The DGX bypasses middleware tenancy and writes parts_catalog directly, so the worker's Neon connection string MUST point at the Autohub Parts_Catalog DB, not Lubes MolasLUBES. Mitigation: assert the connected DB name at worker startup and fail-fast if wrong. This is the DGX-side analogue of the SetCompany(AutohubKey) pin the middleware workers use.
- Observability: a Python systemd timer needs its own logging (a .NET worker would have inherited Serilog). Acceptable — same model as the existing inventory-classifier service.

### Shape to build (DGX-side)
- A Python service (systemd timer/loop) that: (1) on startup asserts it is connected to Autohub Parts_Catalog, fail-fast otherwise; (2) polls oitm for not-yet-deep rows, article-deduped, batch-limited; (3) runs the proven deep pipeline writing the 5 Neon tables (OE chain → oitm_cross_reference, fitment/categories → theirs); (4) marks rows done so the next pass skips them — its own dedupe, independent of the dead DGX request_id cache.
- /enrich_item stays fast/local and unchanged (live path untouched).
- The inline-deep exception on the on-demand single-line endpoint stays as specified (N=1, operator waiting) — it can call the deep pipeline directly since that runs on the DGX.

### Net
DGX-side Python is the smaller, lower-risk build: it reuses proven code, requires no middleware changes, and removes the HTTP-dedupe risks — at the cost of one explicit responsibility (assert the Autohub Neon target at startup) and self-managed logging.

---

## Worker selection logic & filters (resolved 2026-06-27)

The first-pass enrichment was built incrementally (multiple patches/methods), so provenance markers and
`source` labels are inconsistent and cannot be trusted for selection. The worker is therefore
**depth-driven, never marker-driven**. Diagnostic snapshot that drove this (Parts_Catalog Neon):

| Bucket | Count |
|---|---|
| Truly shallow (≤1 OE-type cross-ref, no fitment, no cats) | **545** (228 with article, 317 without) |
| Already rich (>1 OE **or** has fitment/cats) | **8,609** |
| Rich **but `pass_3` marker unset** (the marker trap) | **522** |

The 522 rich-but-unmarked rows are why selection must measure actual depth: a marker-based worker would
reprocess 545 + 522 ≈ 1,067 rows (≈2× RapidAPI volume) and re-touch already-rich rows. Depth-based
selection targets exactly 545.

### Selection
- **Select** rows WHERE: **OE-type cross-refs (`reference_type='oem'`) ≤ 1 AND no `compatible_vehicle` AND
  no `category`.** Measured from the live rows — **never** off the `pass_3` (or any) completion marker.
- **Mark done by re-measuring depth**, not by writing/trusting a marker. The marker is advisory only.

### Routing
- **Has article →** deepen **direct** (TecDoc lookup by article → write OE chain + fitment + cats).
- **No article →** **discover-first** (kit-detect / OEM-bridge / rediscover), then verify. **Fail-soft:** a
  part that can't be discovered/verified is marked **attempted-not-found** so the next pass skips it — never
  an infinite retry loop (expect misses on `non_tecdoc` rows especially).

### Write discipline (protects the SAP ItemName downstream)
- **OE / supersession numbers → `reference_type='oem'`; aftermarket equivalents → `reference_type='iam_equivalent'`.**
  The middleware ItemName and `GetOemCrossReferencesAsync` read **only `'oem'`** — writing IAM numbers as
  `'oem'` pollutes the ItemName with aftermarket part numbers.
- **Option-C OEM noise filter** applied **before any cross-ref write** (strip position/engine tokens —
  FRONT/REAR, `2.0L`, V6… — so junk never reaches `oitm_cross_reference`).
- **Idempotent upsert on `(oitm_id, oem_number, reference_type)`** so a re-run (or a stray reprocess of one
  of the 522) can never duplicate cross-refs.
- `canonical_oem_number` must be a real OE, not the article or a noise token.

### Open verification questions (confirm before/while building)
1. **Inverse trap:** are there rows that are *shallow* but have the marker *set*? Depth-based selection
   handles them by construction, but count them to confirm scale.
2. **Depth definition in storage:** exactly which of the 5 Neon tables/columns define "has fitment" / "has
   cats", so the worker's measured-depth check matches the diagnostic's 545 and the two don't drift.
