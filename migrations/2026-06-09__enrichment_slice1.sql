-- Phase B enrichment, Slice 1 — persist the enrichment outcome on the line and add the
-- 'needs_manual' review state.
--
-- Background/on-demand enrichment now writes its full result back to the line (so the review page,
-- the borrowed-confirmation modal, and bulk-create can read it without re-calling DGX), and failed
-- enrichments + operator rejections land in 'needs_manual' (operator must decide; never silently
-- dropped) per the Slice-1 decisions (Q8/Q9).

ALTER TABLE staging_document_line
    ADD COLUMN IF NOT EXISTS "NeonOitmId"                     BIGINT       NULL,
    ADD COLUMN IF NOT EXISTS "EnrichmentStatus"              VARCHAR(20)  NULL,  -- success | partial | failed
    ADD COLUMN IF NOT EXISTS "EnrichmentConfirmationRequired" BOOLEAN     NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS "EnrichmentErrorCode"           VARCHAR(50)  NULL,
    ADD COLUMN IF NOT EXISTS "EnrichedAt"                    TIMESTAMPTZ  NULL,
    ADD COLUMN IF NOT EXISTS "EnrichmentPayloadJson"         JSONB        NULL;  -- full DGX result, reused by bulk-create

-- Extend the review-status check with 'needs_manual' (failed enrichment / rejected borrowed line).
ALTER TABLE staging_document_line DROP CONSTRAINT IF EXISTS staging_document_line_reviewstatus_chk;
ALTER TABLE staging_document_line
    ADD CONSTRAINT staging_document_line_reviewstatus_chk
    CHECK ("ReviewStatus" IN ('pending', 'matched', 'create_new', 'skip', 'created', 'create_failed', 'needs_manual'));

-- Worker lookup: pending, non-promotional, not-yet-enriched lines on extracted documents.
CREATE INDEX IF NOT EXISTS ix_staging_document_line_needs_enrichment
    ON staging_document_line ("DocumentId")
    WHERE "ReviewStatus" = 'pending' AND "EnrichmentSource" IS NULL AND "IsPromotional" = false;
