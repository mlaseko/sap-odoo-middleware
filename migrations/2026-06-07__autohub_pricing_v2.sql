-- Autohub pricing model v2 — replaces the placeholder 2-band markup model with the operator's
-- actual brand×cost-band ratio model (mirrors the working JS calculator).
--
--   Cost      = supplierPrice(TZS) × 1.25   (markup at ingestion; AutohubPricing:CostMarkupMultiplier)
--   Retail    = ceil( Cost ÷ ratio )         (ratio per supplier-brand × cost-band)
--   Wholesale = floor( Retail − (Retail − Cost)/2 ), kept > Cost   (margin midpoint)
-- Ceiling/floor use the magnitude-based increments in pricing_rounding_rules.
--
-- Brand keys are SUPPLIERS (BORSEHUNG / DPA / OE / VIKA), with DEFAULT as the fallback — NOT vehicle
-- makes. The line's extracted `brand` is matched case-insensitively; unknown brands use DEFAULT.
--
-- Replaces pricing_brand_ratios entirely (old columns PriceBandMin/PriceBandMax/Pl01ToPl03 are gone),
-- so we DROP and recreate. Safe to run once in order after 2026-06-05__autohub_phase_b.sql.

BEGIN;

-- 1. pricing_brand_ratios (Cost ÷ ratio = Retail) ----------------------------
DROP TABLE IF EXISTS pricing_brand_ratios;

CREATE TABLE pricing_brand_ratios (
    "Id"                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "Brand"             VARCHAR(50) NOT NULL,        -- 'BORSEHUNG' | 'DPA' | 'OE' | 'VIKA' | 'DEFAULT'
    "BandMin"           NUMERIC(18, 2) NOT NULL,     -- cost lower bound (inclusive)
    "BandMax"           NUMERIC(18, 2) NULL,         -- cost upper bound (exclusive); NULL = unbounded
    "CostToRetailRatio" NUMERIC(8, 6) NOT NULL,      -- retail = cost / ratio
    "EffectiveFrom"     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    "EffectiveTo"       TIMESTAMPTZ NULL,
    "Notes"             TEXT NULL
);

CREATE INDEX ix_pricing_brand_ratios_lookup
    ON pricing_brand_ratios ("Brand", "BandMin")
    WHERE "EffectiveTo" IS NULL;

-- 5 brands × 8 cost bands. Bands: 0–10k / 10k–25k / 25k–50k / 50k–100k / 100k–200k / 200k–500k /
-- 500k–1M / 1M+.
INSERT INTO pricing_brand_ratios ("Brand", "BandMin", "BandMax", "CostToRetailRatio", "Notes")
VALUES
    -- DEFAULT
    ('DEFAULT', 0,        10000,    0.190000, 'fallback for unknown brands'),
    ('DEFAULT', 10000,    25000,    0.240000, NULL),
    ('DEFAULT', 25000,    50000,    0.270000, NULL),
    ('DEFAULT', 50000,    100000,   0.300000, NULL),
    ('DEFAULT', 100000,   200000,   0.320000, NULL),
    ('DEFAULT', 200000,   500000,   0.345000, NULL),
    ('DEFAULT', 500000,   1000000,  0.370000, NULL),
    ('DEFAULT', 1000000,  NULL,     0.390000, NULL),
    -- BORSEHUNG
    ('BORSEHUNG', 0,       10000,   0.201000, NULL),
    ('BORSEHUNG', 10000,   25000,   0.252000, NULL),
    ('BORSEHUNG', 25000,   50000,   0.295000, NULL),
    ('BORSEHUNG', 50000,   100000,  0.321000, NULL),
    ('BORSEHUNG', 100000,  200000,  0.338000, NULL),
    ('BORSEHUNG', 200000,  500000,  0.365000, NULL),
    ('BORSEHUNG', 500000,  1000000, 0.375000, NULL),
    ('BORSEHUNG', 1000000, NULL,    0.392000, NULL),
    -- DPA
    ('DPA', 0,       10000,   0.185000, NULL),
    ('DPA', 10000,   25000,   0.221000, NULL),
    ('DPA', 25000,   50000,   0.252000, NULL),
    ('DPA', 50000,   100000,  0.285000, NULL),
    ('DPA', 100000,  200000,  0.312000, NULL),
    ('DPA', 200000,  500000,  0.338000, NULL),
    ('DPA', 500000,  1000000, 0.362000, NULL),
    ('DPA', 1000000, NULL,    0.384000, NULL),
    -- OE
    ('OE', 0,       10000,   0.165000, NULL),
    ('OE', 10000,   25000,   0.214000, NULL),
    ('OE', 25000,   50000,   0.258000, NULL),
    ('OE', 50000,   100000,  0.291000, NULL),
    ('OE', 100000,  200000,  0.319000, NULL),
    ('OE', 200000,  500000,  0.346000, NULL),
    ('OE', 500000,  1000000, 0.371000, NULL),
    ('OE', 1000000, NULL,    0.395000, NULL),
    -- VIKA
    ('VIKA', 0,       10000,   0.188000, NULL),
    ('VIKA', 10000,   25000,   0.235000, NULL),
    ('VIKA', 25000,   50000,   0.276000, NULL),
    ('VIKA', 50000,   100000,  0.308000, NULL),
    ('VIKA', 100000,  200000,  0.332000, NULL),
    ('VIKA', 200000,  500000,  0.352000, NULL),
    ('VIKA', 500000,  1000000, 0.369000, NULL),
    ('VIKA', 1000000, NULL,    0.388000, NULL);

-- 2. pricing_rounding_rules (increment by price magnitude) -------------------
DROP TABLE IF EXISTS pricing_rounding_rules;

CREATE TABLE pricing_rounding_rules (
    "Id"        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "MinPrice"  NUMERIC(18, 2) NOT NULL,     -- inclusive lower bound of the price magnitude
    "MaxPrice"  NUMERIC(18, 2) NULL,         -- exclusive upper bound; NULL = unbounded
    "RoundTo"   INT NOT NULL                 -- nearest increment (retail ceils, wholesale floors)
);

INSERT INTO pricing_rounding_rules ("MinPrice", "MaxPrice", "RoundTo")
VALUES
    (0,       10000,    500),
    (10000,   50000,    1000),
    (50000,   200000,   5000),
    (200000,  500000,   10000),
    (500000,  1000000,  25000),
    (1000000, 5000000,  50000),
    (5000000, NULL,     100000);

-- 3. MINI prefix correction --------------------------------------------------
-- The 2026-06-05 phase-B migration was patched to seed 'MINI', but environments that already ran
-- the original (which seeded the 3-char 'MIN') won't re-run it — so repeat the fix here where it is
-- guaranteed to run once. Drop the legacy 'MIN' counter and ensure the 4-char 'MINI' exists;
-- SapSkuCounterRefreshService will set its real value from the live SAP MAX on the next refresh.
DELETE FROM sku_counters WHERE "Prefix" = 'MIN';
INSERT INTO sku_counters ("Prefix", "CurrentValue")
SELECT 'MINI', 10000::bigint
WHERE NOT EXISTS (SELECT 1 FROM sku_counters WHERE "Prefix" = 'MINI');

COMMIT;
