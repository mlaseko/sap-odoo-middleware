-- Part B cleanup — remove internal SKUs leaked into the OEM namespace.
--
-- Contamination: rows in oitm_cross_reference with reference_type='oem' whose oem_number is actually one
-- of our internal SKUs (equals an existing oitm.item_code). Reviewed sample: our LR1xxxxx SKUs sitting as
-- 'oem' on unrelated items (a gasket's SKU across four air-suspension struts, a brake-pad SKU on a
-- mounting). The part-type mismatch + the LR1xxxxx internal range confirm these are leaked SKUs, not
-- coincidental real OEs — so they are safe to delete without per-row review.
--
-- Scope is STRICTLY reference_type='oem'. The 'iam_equivalent' rows that also collide with an item_code
-- (reviewed: 22 of them) are LEGITIMATE aftermarket numbers on the right part family and the bridge
-- excludes them anyway — this script never touches them.
--
-- Run against the Autohub Parts_Catalog Neon DB. Transactional: check the counts, then COMMIT (or
-- ROLLBACK to dry-run).

BEGIN;

-- Preview what will be deleted (leaked internal SKUs in the 'oem' space):
-- SELECT x.oitm_id, owner.item_code AS owning_item, owner.article_number AS owning_article,
--        x.oem_number AS leaked_sku, sku_item.article_number AS leaked_sku_owns_article
-- FROM oitm_cross_reference x
-- JOIN oitm owner    ON owner.id = x.oitm_id
-- JOIN oitm sku_item ON sku_item.item_code = x.oem_number
-- WHERE x.reference_type = 'oem'
-- ORDER BY x.oitm_id;

DELETE FROM oitm_cross_reference x
USING oitm o
WHERE x.reference_type = 'oem'
  AND o.item_code = x.oem_number;   -- oem_number IS one of our internal SKUs -> leaked, remove

-- psql prints DELETE N. Cross-check against the reviewed count (11) before COMMIT.
COMMIT;

-- Note: this cleans the historical leak. Stopping NEW leaks needs the DGX-side source fix (the
-- non_tecdoc/germax import & _upsert_oitm must not write item_code into oitm_cross_reference). The
-- middleware write-guard (this PR) prevents the leak from propagating through middleware writes/ItemName,
-- but the DGX writes parts_catalog directly and must be fixed at source.
