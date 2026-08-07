# Manufacturer resolution for SAP item-code assignment

**Status:** design agreed (DGX ↔ middleware); middleware Part 0 shipped (see “What ships in this PR”).
**Owners:** DGX (auto-resolution + candidates + finalize endpoint + learning table); middleware (hold states, review UI, resolve call, counter cap).

---

## Problem

SAP item codes are structured with a leading **manufacturer / marque code** — `BM` = BMW, `MB` = Mercedes-Benz, `VAG` = VW/Audi/Porsche, etc. That prefix *is* the marque, and DGX (which owns the internal code structure) derives it during enrichment.

When DGX could **not** determine the marque it returned `suggested_sku_prefix = "GEN"` (or empty), and the middleware minted the item under a generic `GEN` counter. On the Germax invoice `INS20260804` (VIKA / DPA / Borsehung, one supplier) this produced 88 mis-prefixed `GEN##` items, and — because the `GEN` counter was unseeded and then hand-seeded and re-run before the dedup guard existed — a batch of duplicates. **The 88 `GEN##` `oitm` rows were deleted from Neon; re-creation is pending** (the SAP-side `GEN` items still exist, and the staging lines in document `643d4876…` still carry `CreatedSku LIKE 'GEN%'` with `WrittenToSapAt` set — see the re-run choreography below).

**Root cause:** a *machine* was allowed to pick a fallback marque bucket. The fix removes that ability and replaces it with a human decision at exactly the point where the machine is genuinely unsure.

---

## The fix is two parts (order matters)

### Part 1 — DGX climbs the resolution ladder it already has data for (primary)

Before ever asking a human, DGX resolves the marque from evidence it already holds, as a **corroboration model** (not strict first-rung-wins):

- matched article’s `oemBrand` majority
- vehicle-fitment marque majority
- OE-structure rule (the framework’s own digit rule — deterministic, derived from the part number)
- invoice brand as a **set prior** (see below)

Rungs **vote**. When they agree, auto-resolve. When a voted marque **conflicts** with the evidence, that conflict is a genuine-ambiguity signal → drop to Part 2, regardless of how high any single share is. This is what earns “N of N *correct*” rather than “N of N *confident*.”

**Brand prior is a marque SET, not a single answer (DGX S5).** VIKA, Borsehung and DPA are **multi-marque** manufacturers — they make VAG *and* BMW *and* Mercedes parts (VAG-dominant).[^brand-set] So the invoice brand names the *family* of marques the maker serves — e.g. `{VAG, BM, MB}`, dominant first — never one marque. The consequence for voting: a voted marque **inside** the brand’s set now **corroborates** (e.g. MB-structured OEMs under Borsehung auto-resolve as `MB`); a voted marque **outside** the set (e.g. Land Rover structure under VIKA) is the genuine conflict and still queues, with the evidence string marking `(OUTSIDE)`. Insufficient-signal lines return up to three candidates, VAG first.

[^brand-set]: Retires the earlier working assumption “Borsehung ⇒ VAG” (and any single-marque-per-brand reading). Authoritative business fact from Mohamed: these brands are multi-marque, VAG-dominant.

> On batch `INS20260804` the ladder is expected to auto-resolve the vast majority with no human involvement. Without Part 1, the `needs_manufacturer` queue would serve the operator a dropdown click for parts a machine can read — and review fatigue is how review states die. Part 1 keeps the queue rare and worth attention.

### Part 2 — Operator handshake (catches what the ladder genuinely can’t)

When the ladder cannot resolve (or rungs conflict), the line is **held** for a human, who selects the marque from a controlled list; DGX then uses that selection to finalize the code. The operator supplies only the *marque*; DGX remains the sole authority for the *code structure*.

**Flow**

1. **Enrich** — middleware calls `/enrich_item` (unchanged trigger).
2. **Unresolved signal** — instead of `suggested_sku_prefix = "GEN"`, DGX returns:
   ```json
   "manufacturer_resolution": {
     "resolved": false,
     "candidates": [
       { "code": "VAG", "label": "VW/Audi/Porsche", "share": 0.00,
         "evidence": "brand 'vika' makes VAG parts (dominant)" },
       { "code": "BM",  "label": "BMW",             "share": 0.00,
         "evidence": "brand 'vika' makes BM parts" },
       { "code": "MB",  "label": "Mercedes-Benz",   "share": 0.00,
         "evidence": "brand 'vika' makes MB parts" }
     ]
   }
   ```
   **Candidate shape is pinned: `{ code, label, share, evidence }` per candidate** — `evidence` (not `reason`) to match the resolved-case block; `share` is per-candidate (a pending ~15-line DGX module patch emits per-candidate shares; as shipped today DGX sends `{code,label}` with a single top-level evidence string). `suggested_sku_prefix` left null. `candidates` may be empty (operator picks from the full list). The example above is an *insufficient-signal* set (three candidates, VAG first, `share: 0.00` but meaningful evidence text); a *conflict* case would carry non-zero shares with one marque marked `(OUTSIDE)`. The **evidence line** is what makes the operator’s click fast and auditable — they confirm a case, not research one.

   **Three rendering rules (Part 2 UI):**
   1. **Render candidates in the order DGX sends — do not re-sort.** The order is the ranking (dominant / most-evidenced first).
   2. **Do not hide `share: 0.0` candidates.** Set-derived candidates legitimately carry zero evidence-share with meaningful evidence text — they are real options (VAG deliberately first), not noise.
   3. **The dropdown must comfortably show 3–4 candidates with their evidence lines** — don’t design for 1–2.

   > **Preservation note:** `EnrichmentResultRouter` stores the enrichment by re-serializing the typed `EnrichmentResponse` — but that record already carries `[JsonExtensionData] Extra`, added to round-trip unknown DGX fields, so `manufacturer_resolution` **already survives storage** (nothing is lost). The typed `ManufacturerResolution` property (shipped separately, PR #265) upgrades that raw round-trip to strongly-typed access for the UI — a convenience, not a data-loss fix.
3. **Hold** — middleware routes the line to `needs_manufacturer` (not creatable) and stores the candidates for the UI. No code assigned.
4. **Operator resolves** — the review UI shows a marque dropdown (candidates first, full list as fallback); operator picks e.g. `VAG`.
5. **Finalize** — middleware calls the dedicated **`POST /resolve_manufacturer`** with the line identity + `manufacturer: "VAG"`. DGX **re-ranks the stored OEM cross-references under that marque** (the ItemName OEM chain is marque-ranked — the `Take(5)` ordering changes with the marque) and returns the **marque package** (v1 shape as live):
   ```json
   { "prefix": "VAG", "suggested_itms_grp_cod": 137, "vehicle_category": "…",
     "ranked_oems": ["…"], "ruling_stored": true }
   ```
   The name and enrichment payload were **already delivered by the original `/enrich_item`** response the middleware holds on the line, so the resolve client **merges the marque package into the held enrichment** — it does NOT expect a second full `item_data`. Idempotent — callable twice safely. (If a future version prefers returning full `item_data` from this endpoint, that's buildable; v1 is the merge shape.)
6. **Create** — the line returns to a normal creatable state; bulk-create proceeds with the resolved prefix. `GEN` is never used.

---

## Goal, sharpened

**GEN is never machine-chosen.** Whether `GEN` remains an operator-*choosable* option for genuine multi-marque universals is a business ruling, not a fallback — and since the fleets are marque-organized, the default is that humans don’t pick it either. It is removed from every automatic path.

---

## Two design decisions (settled)

**(a) DGX owns the authority list AND returns per-line ranked candidates.** `GET /manufacturers` is the single source of valid `{code,label}` pairs (the prefix-is-the-marque model lives in exactly one system, or the lists drift). Per-line candidates come ranked with evidence attached (see step 2). Middleware treats DGX as the source of valid codes.

**(b) Dedicated `/resolve_manufacturer` endpoint — not a re-run of `/enrich_item`.** Re-running full enrichment risks nondeterminism (the donor landscape shifts between calls) and wastes work that already succeeded. More importantly, finalization is not just filling in a prefix: DGX must **re-rank the OEM chain under the chosen marque** before emitting `item_data`. A lightweight endpoint taking line identity + `manufacturer` and returning the complete re-ranked package — idempotent — is the right shape. `manufacturer_override` may also live on `/enrich_item` as a convenience, but the dedicated endpoint is the contract.

---

## Four additions written into the contract

1. **Learning loop is v1, not later.** A curated rulings table `(SupplierArticleNumber, Brand) → marque, decided_by, decided_at`, consulted **before** the ladder, written on **every** operator resolution. One insert, one lookup; identical parts never queue twice. It is the marque analogue of the existing curated-override tables. **Seed it from history on day one** — every already-coded `oitm` row’s `item_code` prefix *is* a marque ruling, so `(article_number, supplier_name) → marque` derived from existing rows gives the loop the whole catalogue as ground truth immediately (and doubles as a consistency audit: history disagreeing with the ladder on a known part is a bug to see before go-live). Lives in DGX; middleware feeds it via the resolve call.

2. **Counter-cap behavior is defined, not undefined.** When a `sku_counters` prefix reaches `MaxAllowed`, minting must **hold** (`prefix_exhausted`, operator extends the range), never silently overrun. (Verified gap: the middleware previously ignored `MaxAllowed` on the mint path — see Part 0.) Example live state: `MB` had ~937 codes left of its ceiling.

3. **Freeze, don’t delete, the `GEN` counter row.** It is the audit trail of this incident; the middleware simply refuses to mint against it (achieved by removing the `GEN` default — `GEN` is never passed to the generator).

4. **The acceptance test asserts correctness, not just GEN-absence.** The failure mode has shifted from a *visible* placeholder (`GEN`) to a *plausible* wrong marque that looks correct. “Zero GEN, N auto-resolved, zero interventions” measures throughput, not correctness — it passes even if the ladder is confidently wrong. Before go-live a human labels ground-truth marques for `INS20260804` **once**, and the test asserts **correct marque per line**. The “single-marque” expectation for that batch must be confirmed by someone other than the resolver’s author. **Do not calibrate the auto/handshake threshold on this batch** — a single-marque, unambiguous document drives the threshold to “always auto-resolve”; calibrate on a mixed multi-marque set, regression-test on this one.

---

## Division of labour

| Concern | Owner |
|---|---|
| Resolution ladder + corroboration/conflict logic | DGX |
| `GET /manufacturers` authority list | DGX |
| Per-line evidence-ranked candidates | DGX |
| `POST /resolve_manufacturer` (re-ranks OEM chain, idempotent) | DGX |
| Learning/rulings table + history backfill + threshold | DGX |
| Detect `resolved:false` → `needs_manufacturer` hold | middleware |
| Render candidates **with evidence** + marque dropdown | middleware |
| Resolve-call + local resolution audit on the line | middleware |
| Remove the `GEN` default (machine-unreachable) | middleware |
| `prefix_exhausted` counter-cap guard | middleware |
| Exclude both holds from bulk-create & document completion | middleware |

Middleware **feeds** the learning table via the resolve call; it does not own it.

---

## What ships in this PR (middleware Part 0 — contract-independent)

These are ours regardless of how the DGX contract lands, and they make `GEN` unreachable today:

- **`GEN` default removed** at both mint sites (`PartsItemProvisioningService`, auto + manual). Auto path with no resolved prefix → `needs_manufacturer` hold; manual path with a blank prefix → validation error (the operator must supply it).
- **Shadow-mode guard (belt-now, braces-at-the-gate).** DGX currently runs `MRES_SHADOW=1`, which for a GEN-class line sends `suggested_sku_prefix: "GEN"` as a *real value through the normal path* — not a null hitting a removed default. So the middleware treats an incoming prefix that is null/blank **or equal to `"GEN"`** (case-insensitive) as unresolved → `needs_manufacturer`. With this, the merge is safe with shadow still on: known marques flow unchanged, GEN-class lines queue, zero wrong mints. The later `MRES_SHADOW=0` flip (gated on the labeling pass) merely changes which brain picks the prefix for the resolvable majority. Manual create likewise rejects `"GEN"` as non-assignable.
- **Counter cap enforced** (`SkuCounterRepository.IncrementAsync`): increments only while `CurrentValue < MaxAllowed` (NULL = uncapped); at the ceiling it throws the new **`SkuCounterExhaustedException`** — a *distinct* type so it is never mistaken for the not-seeded (seed-and-retry) case.
- **Two hold states** `needs_manufacturer` / `prefix_exhausted`: persisted via `RecordHeldAsync` (status + operator-facing reason), shown with their own review pills, tallied apart from failures in bulk-create, excluded from the bulk-create retry set, and blocking document completion.

**Still to build (Part 2 — contract now pinned & stable, no type changes beyond the pinned shape):**

- **DONE (PR #265) — typed `manufacturer_resolution`.** ~~Gating item~~: the block was *not* being dropped — `EnrichmentResponse` already round-trips unknown DGX fields via `[JsonExtensionData] Extra`, so candidates already survived storage. PR #265 adds the typed `ManufacturerResolution` property (`{ resolved, candidates:[{code,label,share,evidence}] }`) for clean strongly-typed access (the field now binds to the property instead of `Extra`). No data-loss gate; the rest of Part 2 was never blocked on it.
- Capture + render candidates per the **three rendering rules** above (order-preserving, show `share:0.0`, 3–4 candidates with evidence lines).
- The `manufacturer_override` request field, the `/resolve_manufacturer` client + per-line resolve endpoint (merge marque package into held enrichment), and the marque dropdown.

Part 1 (the marque-set ladder), `GET /manufacturers`, the learning table, and the threshold are DGX-side.

---

## Re-run choreography for `INS20260804` (to settle before the acceptance run)

**Confirmed state** (diagnostics, 2026-08-07): re-creation has **not** happened. Document `643d4876…` still holds its original bulk-create state — 269 lines created with real marque codes (leave alone), **91** lines still `created` with `GEN##` codes + `WrittenToSapAt` set (fix these), 158 `matched` (leave alone). The `GEN##` `oitm` rows are deleted from Neon; the SAP `GEN` items still exist. So the reset-and-re-run path applies (no fresh Excel upload). **Ordering is load-bearing** — run it in exactly this sequence:

1. **Deploy #264 first.** Re-running on current production (pre-#264) re-mints `GEN` under shadow mode — the original bug. This PR must be live before any re-run.
2. **DGX `MRES_SHADOW=0`** (gated on Mohamed’s labeling pass). With shadow *on*, all 91 land in `needs_manufacturer` holds — safe, but a dead-end until the Part-2 UI exists. With shadow *off*, the ladder assigns real marques (a VAG/BM/MB mix).
3. **Freeze the 91 `GEN##` items in SAP** (`OITM.frozenFor = 'Y'`) so they can’t be reused.
4. **Reset the 91 staging lines** in `643d4876…` keyed on `CreatedSku LIKE 'GEN%'` back to `pending` with create-state **and** enrichment cleared, so the worker re-enriches fresh (marque via the ladder). Leaves the 269 real-coded and 158 matched lines untouched.
5. **Re-run** — worker re-enriches → auto-match → bulk-create. Resolvable lines mint real codes; genuine out-of-set/insufficient-signal lines hold; the dedup guard (already merged) collapses any duplicate pairs.

> Count note: 91 GEN *staging lines* vs 88 *oitm rows* deleted — the 3-row gap is expected (a few lines shared/duplicated `oitm` rows); the reset keys on the staging lines.

## Acceptance test (go-live gate)

Re-run document `643d4876…` / `INS20260804` (via the choreography above):

- **zero** machine-assigned `GEN`
- every auto-resolved line carries the **correct** marque (against Mohamed’s one-time human label — *not* merely non-GEN)
- **expect a marque MIX, not a single marque.** The single-marque (“all VAG”) expectation is **retired** — VIKA/Borsehung/DPA are multi-marque, VAG-dominant, so the batch legitimately resolves to a mix of VAG / BM / MB. In the shadow-ledger comparison, incident lines **GEN67 and GEN91 flip from queued → auto-`BM`/auto-`MB`** (MB/BM structure inside the brand set now corroborates instead of conflicting) — this is an *expected* diff, confirmed by the labeling pass, not a regression.
- the `needs_manufacturer` queue exercised only by genuine out-of-set conflicts / insufficient-signal lines (and the threshold calibrated on a separate mixed set, not this batch)
- a prefix driven to its `MaxAllowed` ceiling produces a `prefix_exhausted` hold, never an overrun
