# Fix: OEM-bridge auto-match stole identities from different articles (Germax)

## Symptom
A Germax invoice (13 new parts) showed 10 lines as **`matched`** and 3 as `pending`, yet none of the 13
existed in SAP B1. The `matched` lines were pointing at **existing but different** Land Rover items.

## Root cause
Identity of a part is **`(supplier_name, article_number)`**. `item_code` / `OITM.ItemCode` (e.g.
`LR100387`) is our **generated internal primary key**, not an article or an OEM. Two code paths reused a
donor's `item_code` as a line's identity based on **supplier only**, ignoring the article:

- **`EnrichmentResultRouter`** — a `borrowed_oem_bridge` / `rapidapi_tecdoc_live` enrichment reaches a
  donor via a **shared OEM** (a *different* article). When that donor was the same supplier (GERMAX) and
  already had an `item_code`, the C1 branch marked the line `matched` to the donor's SKU.
- **`AutoMatchService`** Tier-1 — a same-supplier OEM cross-reference hit returned `matched` regardless of
  article.

Because Germax's catalog shares OEM supersession chains, distinct `GL####` SKUs collided onto one item
(e.g. `GL0722`, `GL0071`, `GL0070` all → `LR100387`, whose real article is `13-00574-SX`).

## The fix (Part A — code)
Enforce the invariant: **auto-match / reuse-or-write-to-donor only when supplier AND article match; a
shared OEM alone is never identity.** `item_code` is never used as a match key.

- `EnrichmentResultRouter.ApplyAsync` now takes the line's article; a **same-supplier but different-article**
  donor routes to **create-new own-identity** (borrow enrichment, mint a fresh SKU) instead of C1/C2.
- `AutoMatchService` Tier-1 only matches when the OEM donor's `article_number` equals the line's article;
  otherwise it falls through to Tier-2 (exact article) / pending.
- `OitmMatchRepository` now returns the donor `article_number` so Tier-1 can gate on it.

**Not changed / not special-cased:** TecDoc-direct auto-match (same article) still works; cross-supplier
create-new is unchanged; the DGX Germax scrape, `neon_germax_products`, the `non_tecdoc` source, and the
Tier-2 germax-catalog branch are untouched. The gate is source- and supplier-agnostic, so non-Germax flows
are unaffected — behavior only changes when the donor's article differs from the line's.

Tests: `EnrichmentResultRouterTests`, `AutoMatchServiceTests` (same-supplier-different-article →
create-new, never `matched`).

## Deploy + remediate (Part C — ordered)
1. **Merge & build on the Windows box**, run `dotnet test`, then `Restart-Service SapOdooMiddleware`.
   (No CI — the Windows publish is the build gate.)
2. **Audit the blast radius** — use the **categorized v2**:
   `migrations/2026-07-13__audit_oem_bridge_article_mismatch_categorized.sql`.
   - ⚠️ The original article-only audit **over-flags Germax**: Germax `oitm` rows are keyed by the OE
     number in `article_number` (e.g. `0 986 494 440`), not the `GL####` SKU — that mapping lives in
     `neon_germax_products`. So `donor.article ≠ line article` is *always* true for Germax, even for a
     **legitimate** `tier2_article` catalog match. The v2 audit classifies by `MatchStrategy`:
     `tier2_article` = **LEGIT (leave)**; `tier1_oem` / `*_auto_match` / create-new-but-`matched` = **WRONG (reset)**.
3. **Remediate** with **v2**: `migrations/2026-07-13__remediate_oem_bridge_article_mismatch_v2.sql` — resets
   only the WRONG-strategy lines to `pending` (enrichment cleared) so the **fixed** worker re-resolves them;
   catalogued `GL####` re-match via Tier-2, uncatalogued route to create-new. `tier2_article` is never reset.
   - ⚠️ Do **not** flip `matched` → `create_new` directly: the stale `*_auto_match` strategy would make
     `ProvisionAsync` write the new SKU onto the donor row and corrupt a different part.
4. In the review UI: **Confirm all & Create**, then **Bulk Create**.
5. **Verify:** each line becomes a NEW SAP item keyed by `U_Article_No = GL####`; donor `LR100xxx` items
   are untouched; the next invoice for these `GL####` auto-matches via Tier-2 (exact article).

## Follow-ups (Part B — staged, tracked separately)
Confirmed during diagnosis but out of scope for this change. Part A already neutralises the *matching*
harm of the contamination below (a leaked-SKU bridge hit can no longer auto-match, because the donor's
article won't equal the line's); Part B is about ItemName/cross-ref purity and stopping the leak at source.

**Contamination review — `oitm_cross_reference` rows whose `oem_number` equals an existing `item_code`
(33 rows reviewed):**
- **22 `iam_equivalent` rows — legitimate, leave alone.** Real aftermarket part numbers that coincidentally
  also serve as some other item's internal SKU; the cross-ref itself is correct (right part family) and the
  bridge already excludes `iam_equivalent`, so they cannot cause the collapse. Deleting them would remove
  valid cross-references.
- **11 `oem` rows — genuine contamination, cleanable (unambiguous).** Internal `LR1xxxxx` SKUs leaked into
  `reference_type='oem'` on unrelated items (e.g. a gasket's SKU spread across four air-suspension struts, a
  brake-pad SKU on a mounting). The part-type mismatch plus the `LR1xxxxx` internal range confirms these are
  leaked SKUs, not coincidental real OEs — so no per-row review is needed. These are bridge-visible and are
  what can pollute a borrowed item's ItemName/cross-refs.

**Part B work:**
- **[done — this branch] Clean the 11 `oem` rows**: `migrations/2026-07-13__cleanup_internal_sku_leaked_as_oem.sql`
  deletes `oitm_cross_reference` where `reference_type='oem'` AND `oem_number` equals an existing
  `oitm.item_code`. Strictly `reference_type='oem'` — the 22 legit `iam_equivalent` rows are never touched.
- **[done — this branch] Write-guard**: `NeonBridgeService.CreateOwnIdentityRowAsync` / `CreateFreshRowAsync`
  (copy/write) and `GetOemCrossReferencesAsync` (ItemName read) now exclude any token equal to an existing
  `item_code`, so a leaked SKU can never propagate into a new item's cross-refs / ItemName.
- **[DGX-side — not in this repo] Source fix**: the DGX writes `parts_catalog` directly, bypassing the
  middleware guard, so the leak must also be stopped at source. On `spark-09cc`, in the `_upsert_oitm`
  cross-reference write inside `~/Inventory_Management_Tool/classifier_service.py`, skip any OEM token that
  equals an existing `oitm.item_code` before inserting it as `reference_type='oem'`. Find it with:
  `grep -n "oitm_cross_reference\|reference_type\|_upsert_oitm" ~/Inventory_Management_Tool/classifier_service.py`
- `neon_germax_products` code-existence validation against SAP (separate, tracked).

> Trade-off noted: the guard/cleanup key on "equals an existing `item_code`". A real OE number that
> coincidentally equals one of our SKUs in the shared `LR` range would also be dropped from an ItemName —
> a cosmetic loss, preferred over keeping a leaked internal SKU. The reviewed 11 rows are confirmed leaks
> (part-type mismatch), not coincidental OEs.
