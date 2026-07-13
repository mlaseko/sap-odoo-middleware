-- AUDIT (read-only) — find parts lines wrongly auto-matched to a DIFFERENT article's SAP item.
--
-- Root cause (fixed in code): a shared-OEM (borrowed_oem_bridge / rapidapi) or Tier-1 OEM hit was
-- allowed to mark a line 'matched' to a donor that is a DIFFERENT article — reusing that donor's
-- internal SKU (item_code, our generated primary key) as this line's identity. Identity is
-- (supplier_name, article_number); a shared OEM alone is NOT identity.
--
-- This query lists every currently-'matched' line whose matched SAP item carries an article_number
-- different from the line's own SupplierArticleNumber. Those are the false matches to remediate.
-- Join is by MatchedItemCode -> oitm.item_code, so it catches BOTH the enrichment router path
-- (EnrichmentSource set) and the AutoMatchService Tier-1 path (MatchStrategy='tier1_oem', NeonOitmId null).
--
-- Run against the Autohub Parts_Catalog Neon DB. Read-only. Safe to run any time.

SELECT
    l."DocumentId",
    l."LineNumber",
    l."SupplierArticleNumber"          AS line_article,
    l."Brand",
    l."ReviewStatus",
    l."MatchStrategy",
    l."EnrichmentSource",
    l."MatchedItemCode",
    o.id                              AS donor_oitm_id,
    o.article_number                  AS donor_article,
    o.supplier_name                   AS donor_supplier
FROM public.staging_document_line l
JOIN public.oitm o
      ON o.item_code = l."MatchedItemCode"
WHERE l."ReviewStatus" = 'matched'
  -- article mismatch = wrong match (NULL donor article is also a mismatch: identity unprovable)
  AND lower(btrim(o.article_number)) IS DISTINCT FROM lower(btrim(l."SupplierArticleNumber"))
ORDER BY l."DocumentId", l."LineNumber";

-- Scope check for the reported invoice only:
-- ... AND l."DocumentId" = '5f34d0cf-d59e-48e4-9054-2ea05849347b'

-- Count by document (blast-radius summary):
-- SELECT l."DocumentId", COUNT(*) AS wrong_matches
-- FROM public.staging_document_line l
-- JOIN public.oitm o ON o.item_code = l."MatchedItemCode"
-- WHERE l."ReviewStatus" = 'matched'
--   AND lower(btrim(o.article_number)) IS DISTINCT FROM lower(btrim(l."SupplierArticleNumber"))
-- GROUP BY l."DocumentId" ORDER BY wrong_matches DESC;
