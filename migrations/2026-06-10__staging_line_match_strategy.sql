-- Phase B Slice 1 — Path C1 (borrowed auto-match): record HOW each line was resolved, so the review
-- UI can badge borrowed auto-matches and we can tell creation paths apart later.
--
-- Values written today:
--   borrowed_oem_bridge_auto_match  — donor oitm already had a SAP item_code; line auto-matched (C1)
--   borrowed_oem_bridge_create_new  — borrowed enrichment, donor not yet in SAP (C2, needs create)
--   enrichment_direct_auto_match    — TecDoc-direct row already had a SAP item_code; auto-matched
--   enrichment_direct               — TecDoc-direct, needs create
--   unmatched                       — failed/partial enrichment
-- (tier1_oem / tier2_article / germax_scraped / manual are reserved for the other match paths.)

ALTER TABLE staging_document_line
    ADD COLUMN IF NOT EXISTS "MatchStrategy" TEXT NULL;
