-- Phase B Slice 1.6 — supplier-identity enforcement.
--
-- Auto-match and the enrichment router must not link an invoice line to an existing SAP item that
-- belongs to a DIFFERENT supplier (a SAP item represents exactly one supplier+article). Cross-supplier
-- OEM hits now route to create-new (borrowed enrichment); vehicle-group brands (VAG/BMW/MB/…) route to
-- a new 'needs_confirmation' state where the operator picks use-existing / create-new / skip.

-- 1) Allow the new review state.
ALTER TABLE staging_document_line DROP CONSTRAINT IF EXISTS staging_document_line_reviewstatus_chk;
ALTER TABLE staging_document_line
    ADD CONSTRAINT staging_document_line_reviewstatus_chk
    CHECK ("ReviewStatus" IN ('pending', 'matched', 'create_new', 'skip', 'created',
                              'create_failed', 'needs_manual', 'needs_confirmation'));

-- 2) Suggested-donor columns drive the confirmation modal. (MatchStrategy is unconstrained TEXT — no
--    constraint to extend; the new strategy values are written directly.)
ALTER TABLE staging_document_line
    ADD COLUMN IF NOT EXISTS "SuggestedDonorItemCode" TEXT   NULL,
    ADD COLUMN IF NOT EXISTS "SuggestedDonorOitmId"   BIGINT NULL,
    ADD COLUMN IF NOT EXISTS "SuggestedDonorSupplier" TEXT   NULL;
