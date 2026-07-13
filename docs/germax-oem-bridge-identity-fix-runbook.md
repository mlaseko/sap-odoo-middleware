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
2. **Audit the blast radius** (read-only): `migrations/2026-07-13__audit_oem_bridge_article_mismatch.sql`
   lists every currently-`matched` line whose matched item's article ≠ the line's article.
3. **Remediate** with `migrations/2026-07-13__remediate_oem_bridge_article_mismatch.sql` — resets those
   lines to `pending` with enrichment cleared so the **fixed** worker re-routes them to create-new.
   Default scope is the reported document `5f34d0cf-…`; broaden after reviewing the audit.
   - ⚠️ Do **not** flip `matched` → `create_new` directly: the stale `*_auto_match` strategy would make
     `ProvisionAsync` write the new SKU onto the donor row and corrupt a different part.
4. In the review UI: **Confirm all & Create**, then **Bulk Create**.
5. **Verify:** each line becomes a NEW SAP item keyed by `U_Article_No = GL####`; donor `LR100xxx` items
   are untouched; the next invoice for these `GL####` auto-matches via Tier-2 (exact article).

## Follow-ups (Part B — staged, tracked separately)
Confirmed during diagnosis but out of scope for this change:
- **Internal SKUs leaked into `oitm_cross_reference` as `reference_type='oem'`** (our `LR100xxx` PKs sitting
  in the OEM namespace; `LR` prefix collides with real `LR######` OE numbers). Add a write-guard (never
  persist an OEM equal to an existing `item_code`) + staged cleanup (safe: self-references where
  `oem_number = the row's own item_code`; review: cross-row collisions, which may be coincidental real OEs).
- **DGX-side source fix**: stop the `non_tecdoc`/germax import & `_upsert_oitm` from writing `item_code`
  into cross-references.
- `neon_germax_products` code-existence validation against SAP; `iam_equivalent` junk cleanup.
