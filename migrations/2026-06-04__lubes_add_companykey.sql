-- Autohub Phase A — defensive CompanyKey column on the existing Lubes staging tables.
-- Run this against the MolasLUBES Neon branch (the existing Lubes database).
-- Additive and idempotent (ADD COLUMN IF NOT EXISTS); safe to re-run and safe on rollback.
--
-- This is belt-and-suspenders: tenant boundaries are enforced at the connection-string level,
-- but stamping every row with its CompanyKey makes audits/recovery trivial if a query ever
-- reaches the wrong database. Lubes rows default to 'Lubes'. The Lubes code does not read or
-- write this column in Phase A — it exists purely for safety/symmetry with parts_catalog.

BEGIN;

ALTER TABLE public."staging_document"
    ADD COLUMN IF NOT EXISTS "CompanyKey" text NOT NULL DEFAULT 'Lubes';

ALTER TABLE public."staging_document_line"
    ADD COLUMN IF NOT EXISTS "CompanyKey" text NOT NULL DEFAULT 'Lubes';

COMMIT;
