-- Lubes provisioning — SAP-family classification audit trail (Layer 3).
-- Run this against the MolasLUBES Neon branch BEFORE deploying the matching middleware build
-- (NeonProductRepository.UpsertProductAsync references these columns).
-- Additive and idempotent (ADD COLUMN IF NOT EXISTS); all columns nullable so existing rows and
-- any non-Lubes writer are unaffected. Safe to re-run.
--
-- Columns:
--   FamilyConfidence      — the SAP-family classification confidence (1.0 for a Layer 1 override).
--   FamilyNeedsReview     — DGX's needs_review flag as returned (TRUE even when Layer 2 accepted it,
--                           so the audit shows what was flagged). FALSE for Layer 1 overrides.
--   FamilyOverrideReason  — why the gate was bypassed: 'layer1: <rule>' (deterministic name rule) or
--                           'layer2: dgx needs_review accepted at confidence 0.NN'. NULL when the
--                           classification passed cleanly with no override.
--
-- Audit query example — everything that bypassed the SAP-family gate:
--   SELECT "ItemCode","ItemGroupCode","FamilyConfidence","FamilyOverrideReason"
--   FROM public."NeonProducts"
--   WHERE "FamilyOverrideReason" IS NOT NULL
--   ORDER BY "SyncedAt" DESC;

BEGIN;

ALTER TABLE public."NeonProducts"
    ADD COLUMN IF NOT EXISTS "FamilyConfidence"     numeric,
    ADD COLUMN IF NOT EXISTS "FamilyNeedsReview"    boolean,
    ADD COLUMN IF NOT EXISTS "FamilyOverrideReason" text;

COMMIT;
