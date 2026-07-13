-- REMEDIATION — reset wrongly auto-matched parts lines back to 'pending' with enrichment cleared,
-- so the FIXED enrichment worker re-routes them to create-new (own-identity) and Bulk Create can
-- mint the correct new SAP items.
--
-- PRECONDITION: deploy the code fix FIRST (article-identity gate in EnrichmentResultRouter +
-- AutoMatchService). If you reset before deploying, the old logic simply re-collapses the lines.
--
-- WHY reset instead of flipping 'matched' -> 'create_new' directly:
--   the stored MatchStrategy on these lines is a *_auto_match value, which IsCrossSupplierStrategy()
--   returns FALSE for. Creating from that state would make ProvisionAsync LINK the new SKU onto the
--   donor row (a DIFFERENT part) and corrupt it. Clearing EnrichmentSource routes the line through the
--   fixed worker, which assigns a correct own-identity create-new strategy.
--
-- The cleared column set mirrors PartsReviewRepository's existing "reset to pending" method, PLUS
-- MatchedItemCode and the SuggestedDonor* fields (which a 'matched' line carries and a re-route must drop).
--
-- Run against the Autohub Parts_Catalog Neon DB. Transactional: verify the row count, then COMMIT
-- (or ROLLBACK to dry-run).

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
  -- DEFAULT SCOPE: the reported invoice only. Remove/broaden this line (after reviewing the audit
  -- output) to remediate everything the audit surfaced.
  AND l."DocumentId" = '5f34d0cf-d59e-48e4-9054-2ea05849347b';

-- Inspect the affected count printed by psql (UPDATE N). If it matches the audit, COMMIT; else ROLLBACK.
COMMIT;

-- After COMMIT:
--   1) The background enrichment worker re-enriches these pending lines and (under the fixed router)
--      routes them to create-new own-identity (borrowed_*/rapidapi_* cross_supplier_create_new).
--   2) In the review UI: Confirm all & Create (or bulk-confirm-create-new) then Bulk Create.
--   3) Verify: each becomes a NEW SAP item keyed by U_Article_No = its GL#### article; the donor rows
--      (e.g. the LR100xxx internal SKUs) are left untouched.
