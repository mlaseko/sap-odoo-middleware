-- REMEDIATION v2 — SUPERSEDES the article-only remediation.
--
-- Resets ONLY the genuine wrong-collapse matches (tier1_oem OEM-collapses, enrichment *_auto_match, and
-- create-new-but-'matched' anomalies) back to 'pending' with enrichment cleared, so the FIXED worker
-- re-resolves them. It deliberately does NOT touch tier2_article — those are authoritative catalog /
-- exact-article matches (for Germax the GL#### -> internal-SKU mapping in neon_germax_products), and
-- resetting them would be needless churn and could risk a duplicate if the catalog has a gap.
--
-- PRECONDITION: the code fix (PR #240) must be deployed first, or the old logic re-collapses on re-enrich.
-- Run AFTER reviewing the categorized audit (2026-07-13__audit_oem_bridge_article_mismatch_categorized.sql).
--
-- Run against the Autohub Parts_Catalog Neon DB. Transactional: verify the count, then COMMIT (or ROLLBACK).

BEGIN;

UPDATE public.staging_document_line AS l
SET "ReviewStatus"                   = 'pending',
    "MatchedItemCode"                = NULL,
    "MatchStrategy"                  = NULL,
    "EnrichmentSource"               = NULL,
    "BorrowedFromArticle"            = NULL,
    "BorrowedFromSupplier"           = NULL,
    "NeonOitmId"                     = NULL,
    "EnrichmentStatus"               = NULL,
    "EnrichmentErrorCode"            = NULL,
    "EnrichedAt"                     = NULL,
    "EnrichmentPayloadJson"          = NULL,
    "EnrichmentConfirmationRequired" = false,
    "EnrichmentConfirmedBy"          = NULL,
    "EnrichmentConfirmedAt"          = NULL,
    "SuggestedDonorItemCode"         = NULL,
    "SuggestedDonorOitmId"           = NULL,
    "SuggestedDonorSupplier"         = NULL
FROM public.oitm o
WHERE l."MatchedItemCode" = o.item_code
  AND l."ReviewStatus" = 'matched'
  AND lower(btrim(o.article_number)) IS DISTINCT FROM lower(btrim(l."SupplierArticleNumber"))
  -- WRONG-collapse strategies only. tier2_article (authoritative) is intentionally excluded.
  AND (
        l."MatchStrategy" = 'tier1_oem'
     OR l."MatchStrategy" LIKE '%\_auto\_match'
     OR l."MatchStrategy" LIKE '%\_create\_new'   -- create-new strategy stuck on a 'matched' row (anomaly)
      )
  -- OPTIONAL SCOPE: uncomment to remediate one document at a time.
  -- AND l."DocumentId" IN ('3574bfe3-2869-4154-9157-07ef20ad67f3', '4170b3e0-2a6b-42ec-91ef-40978b6374e6')
;

-- psql prints UPDATE N. Compare with the categorized audit's WRONG/ANOMALY counts, then COMMIT.
COMMIT;

-- After COMMIT: the fixed worker re-enriches/re-matches these lines. Catalogued GL#### re-match via Tier-2
-- (correct item); uncatalogued ones route to create-new own-identity. Then in the UI:
-- Confirm all & Create -> Bulk Create. Verify new items are keyed by U_Article_No = GL####; donors untouched.
