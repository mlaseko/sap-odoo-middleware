-- Pricing engine additions (26-27 Sep 2026 review of settled vs formula prices):
--
-- 1. pricing_brand_floors — minimum selling price (PL03 floor) per brand, applied AFTER
--    the band-ratio calculation and BEFORE rounding. Cost-plus breaks down on cheap
--    parts (VIKA items costing 2-4k TZS settle at 40k+), so the floor encodes "a small
--    part does not sell below X regardless of cost". Versioned like pricing_brand_ratios:
--    the engine reads WHERE "EffectiveTo" IS NULL; change a floor by closing the active
--    row (set "EffectiveTo" = NOW()) and inserting a new one. Exact-brand only — there
--    is deliberately NO DEFAULT fallback, so brands without a measured floor get none.
--
-- 2. pricing_recalc_flags — worklist for the pricing team. A PATCH that changes the
--    brand (U_MdlTEST) leaves the item priced by the OLD brand's ratio; the middleware
--    flags it here (one open row per item, debounced) instead of silently repricing,
--    so hand-set prices are never overwritten by a metadata correction. Resolve a row
--    by setting resolved_at (and optionally resolved_by/notes) after repricing or
--    deciding the price stands.
--
-- Run against the Autohub Neon (parts_catalog) database with a role that may CREATE in
-- schema public. If the app role differs from the migration role, uncomment the GRANTs
-- and put the app role's name in.

BEGIN;

-- 1. Minimum selling price per brand -----------------------------------------
CREATE TABLE IF NOT EXISTS pricing_brand_floors (
    "Id"            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "Brand"         VARCHAR(50)   NOT NULL,        -- supplier key, matched case-insensitively
    "MinSellPrice"  NUMERIC(18,2) NOT NULL,        -- PL03 floor in TZS, pre-rounding
    "EffectiveFrom" TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    "EffectiveTo"   TIMESTAMPTZ   NULL,
    "Notes"         TEXT          NULL
);

-- One ACTIVE floor per brand (case-insensitive).
CREATE UNIQUE INDEX IF NOT EXISTS ux_pricing_brand_floors_active
    ON pricing_brand_floors (UPPER("Brand"))
    WHERE "EffectiveTo" IS NULL;

-- Initial value from the VIKA review: settled PL03 for CIF<10k has median 55,000 and
-- 30/33 items already at or above 40,000. With the new 0.130 lowest-band ratio the
-- floor binds only on the cheapest parts, so the two rules do not fight.
INSERT INTO pricing_brand_floors ("Brand", "MinSellPrice", "Notes")
SELECT 'VIKA', 40000.00,
       '26 Sep 2026 review: 134/268 VIKA items hand-repriced upward; floor covers the cheap-part band.'
WHERE NOT EXISTS (
    SELECT 1 FROM pricing_brand_floors
    WHERE UPPER("Brand") = 'VIKA' AND "EffectiveTo" IS NULL
);

-- 2. Brand-change reprice worklist -------------------------------------------
CREATE TABLE IF NOT EXISTS pricing_recalc_flags (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    item_code     VARCHAR(50)  NOT NULL,
    old_brand     VARCHAR(100) NULL,             -- U_MdlTEST before the edit (the brand the price was built on)
    new_brand     VARCHAR(100) NULL,             -- U_MdlTEST after the edit
    pl01          NUMERIC(18,2) NULL,            -- prices at flag time, for the reviewer
    pl03          NUMERIC(18,2) NULL,
    pl05          NUMERIC(18,2) NULL,
    requested_by  VARCHAR(100) NULL,
    flagged_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    resolved_at   TIMESTAMPTZ  NULL,
    resolved_by   VARCHAR(100) NULL,
    notes         TEXT         NULL
);

-- One OPEN flag per item; repeat brand edits merge into it (old_brand keeps the
-- original price-basis brand, new_brand follows the latest edit).
CREATE UNIQUE INDEX IF NOT EXISTS ux_pricing_recalc_flags_open
    ON pricing_recalc_flags (item_code)
    WHERE resolved_at IS NULL;

-- GRANT SELECT, INSERT, UPDATE ON pricing_brand_floors  TO <app_role>;
-- GRANT SELECT, INSERT, UPDATE ON pricing_recalc_flags  TO <app_role>;
-- GRANT USAGE ON ALL SEQUENCES IN SCHEMA public         TO <app_role>;

COMMIT;

-- Verification:
--   SELECT * FROM pricing_brand_floors WHERE "EffectiveTo" IS NULL;   -- 1 row: VIKA 40000
--   SELECT COUNT(*) FROM pricing_recalc_flags;                        -- 0
