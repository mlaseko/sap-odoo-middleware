-- AUDIT v2 (read-only, categorized) — SUPERSEDES the article-only audit for Germax.
--
-- WHY v2: Germax oitm rows are keyed by the OE/foreign supplier number in article_number (e.g.
-- '0 986 494 440', '13-00574-SX'), NOT by the Germax GL#### SKU. The GL#### -> internal-SKU mapping
-- lives in neon_germax_products (the germax catalog). So `donor.article_number <> line article` is ALWAYS
-- true for Germax — even for a LEGITIMATE tier2_article match resolved through the catalog. The
-- article-only audit therefore over-flags. This version classifies by MatchStrategy so remediation only
-- touches the genuine wrong-collapses and LEAVES the authoritative catalog/exact-article matches.
--
--   tier2_article        -> LEGIT: exact article OR germax-catalog (GL#### -> our item). Authoritative
--                           identity. DO NOT reset (a reset would just re-match, or risk a duplicate if
--                           the catalog has a gap). Leave.
--   tier1_oem            -> WRONG: matched on a shared OEM, not identity (the collapse). Reset -> the
--                           fixed matcher re-resolves via the catalog, or creates a new item.
--   *_auto_match         -> WRONG: enrichment auto-matched on borrowed data (borrowed_oem_bridge_auto_match,
--                           germax_local_auto_match, rapidapi_tecdoc_live_auto_match, enrichment_direct_auto_match).
--                           Reset -> fixed router routes to create-new own-identity.
--   *_create_new (matched)-> ANOMALY: a create-new strategy sitting on a 'matched' row (pre-fix/backfill
--                           residue). Reset with the WRONG set.
--
-- Run against the Autohub Parts_Catalog Neon DB. Read-only.

SELECT
    l."DocumentId",
    l."LineNumber",
    l."SupplierArticleNumber"          AS line_article,
    l."Brand",
    l."MatchStrategy",
    l."EnrichmentSource",
    l."MatchedItemCode",
    o.article_number                  AS donor_article,
    o.supplier_name                   AS donor_supplier,
    CASE
        WHEN l."MatchStrategy" = 'tier2_article'                THEN 'LEGIT_catalog_or_exact_article__leave'
        WHEN l."MatchStrategy" = 'tier1_oem'                    THEN 'WRONG_oem_collapse__reset'
        WHEN l."MatchStrategy" LIKE '%\_auto\_match'            THEN 'WRONG_enrichment_autofix__reset'
        WHEN l."MatchStrategy" LIKE '%\_create\_new'            THEN 'ANOMALY_createnew_but_matched__reset'
        ELSE 'OTHER__review'
    END                               AS verdict
FROM public.staging_document_line l
JOIN public.oitm o
      ON o.item_code = l."MatchedItemCode"
WHERE l."ReviewStatus" = 'matched'
  AND lower(btrim(o.article_number)) IS DISTINCT FROM lower(btrim(l."SupplierArticleNumber"))
ORDER BY verdict, l."DocumentId", l."LineNumber";

-- Summary by verdict (blast radius that actually needs remediation):
-- SELECT
--   CASE
--     WHEN l."MatchStrategy" = 'tier2_article' THEN 'LEGIT__leave'
--     WHEN l."MatchStrategy" = 'tier1_oem' THEN 'WRONG_oem_collapse'
--     WHEN l."MatchStrategy" LIKE '%\_auto\_match' THEN 'WRONG_enrichment_autofix'
--     WHEN l."MatchStrategy" LIKE '%\_create\_new' THEN 'ANOMALY_createnew_but_matched'
--     ELSE 'OTHER'
--   END AS verdict,
--   COUNT(*) n
-- FROM public.staging_document_line l
-- JOIN public.oitm o ON o.item_code = l."MatchedItemCode"
-- WHERE l."ReviewStatus" = 'matched'
--   AND lower(btrim(o.article_number)) IS DISTINCT FROM lower(btrim(l."SupplierArticleNumber"))
-- GROUP BY 1 ORDER BY n DESC;
