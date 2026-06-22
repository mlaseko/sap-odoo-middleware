-- Phase A hardening: live extraction progress.
-- Adds per-page progress tracking columns to staging_document so the Detail page can show
-- which page is processing, percentage complete, elapsed time, and ETA.
-- Run on the Neon production branch after this migration is merged.

BEGIN;

ALTER TABLE public.staging_document
    ADD COLUMN IF NOT EXISTS "PagesProcessed"        integer       NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "CurrentPageStartedAt"  timestamptz   NULL,
    ADD COLUMN IF NOT EXISTS "LastPageDurationSec"   numeric(8,2)  NULL;

-- Backfill: existing rows take the 0 / NULL defaults above.

COMMIT;
