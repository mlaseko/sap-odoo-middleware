-- Lubes staging_document.Status check constraint was missing the application's end-state values.
-- The app writes 'reviewed' (StagingDocumentRepository.MarkReviewedAsync) and 'completed', but the
-- original constraint only allowed uploaded/extracting/extracted/failed — so Complete Review hit a
-- 23514 check_violation. (Latent since Complete Review was added; only reachable now that the
-- browser API-key injection lets the request actually reach the controller.)
--
-- Run on the Lubes database (the one whose staging_document has staging_document_Status_check).
-- Idempotent: drops the existing constraint if present, then re-adds the complete set.

BEGIN;

ALTER TABLE staging_document DROP CONSTRAINT IF EXISTS "staging_document_Status_check";

ALTER TABLE staging_document
    ADD CONSTRAINT "staging_document_Status_check"
    CHECK ("Status" IN ('uploaded', 'extracting', 'extracted', 'reviewed', 'completed', 'failed'));

COMMIT;
