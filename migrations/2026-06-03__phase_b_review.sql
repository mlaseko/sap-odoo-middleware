-- Phase B: review & bulk-create state.
-- Adds per-document review/auto-match state and per-line review/match/create state.
-- Run on the Neon production branch after merge, BEFORE deploying the rebuilt middleware.

BEGIN;

-- Per-document review state
ALTER TABLE public.staging_document
    ADD COLUMN IF NOT EXISTS "ReviewedAt"        timestamptz   NULL,
    ADD COLUMN IF NOT EXISTS "ReviewedBy"        text          NULL,
    ADD COLUMN IF NOT EXISTS "AutoMatchedAt"     timestamptz   NULL,
    ADD COLUMN IF NOT EXISTS "AutoMatchedCount"  integer       NOT NULL DEFAULT 0;

-- Per-line review state
ALTER TABLE public.staging_document_line
    ADD COLUMN IF NOT EXISTS "ReviewStatus"       text         NOT NULL DEFAULT 'pending',
    ADD COLUMN IF NOT EXISTS "MatchedSku"         text         NULL,
    ADD COLUMN IF NOT EXISTS "CreatedSku"         text         NULL,
    ADD COLUMN IF NOT EXISTS "CreatedAt"          timestamptz  NULL,
    ADD COLUMN IF NOT EXISTS "CreateErrorMessage" text         NULL,
    ADD COLUMN IF NOT EXISTS "EditedAt"           timestamptz  NULL,
    ADD COLUMN IF NOT EXISTS "EditedBy"           text         NULL;

-- Allowed values: pending, matched, create_new, skip, created, create_failed
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'staging_document_line_reviewstatus_chk'
    ) THEN
        ALTER TABLE public.staging_document_line
            ADD CONSTRAINT staging_document_line_reviewstatus_chk
            CHECK ("ReviewStatus" IN ('pending', 'matched', 'create_new', 'skip', 'created', 'create_failed'));
    END IF;
END $$;

-- Index for the review UI's hot path
CREATE INDEX IF NOT EXISTS ix_staging_document_line_documentid_reviewstatus
    ON public.staging_document_line ("DocumentId", "ReviewStatus");

COMMIT;
