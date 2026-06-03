-- Autohub Phase B (slice 1) — foundation tables + staging audit columns.
-- Run against the parts_catalog Neon branch (NOT MolasLUBES). Additive and idempotent.
--
-- Adds: forex_rate (manual TZS conversion rates), sku_counters (atomic per-prefix ItemCode
-- counters), pricing_brand_ratios (PL01->PL03 band ratios), and enrichment/pricing audit columns
-- on staging_document_line. Seed values are placeholders — the operator confirms/edits before
-- go-live (especially sku_counters, which must be seeded from current SAP MAX per prefix).

BEGIN;

-- 4.1 forex_rate ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS forex_rate (
    "Id"            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "CurrencyCode"  VARCHAR(3) NOT NULL,
    "RateToTzs"     NUMERIC(18, 6) NOT NULL,        -- 1 unit of CurrencyCode = N TZS
    "EffectiveFrom" TIMESTAMPTZ NOT NULL,
    "EffectiveTo"   TIMESTAMPTZ NULL,               -- NULL = current rate
    "CreatedBy"     VARCHAR(100) NOT NULL,
    "CreatedAt"     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    "Notes"         TEXT NULL,
    CONSTRAINT forex_rate_currency_check CHECK ("CurrencyCode" IN ('USD','AED','GBP','EUR'))
);

CREATE INDEX IF NOT EXISTS ix_forex_rate_lookup
    ON forex_rate ("CurrencyCode", "EffectiveFrom" DESC)
    WHERE "EffectiveTo" IS NULL;

INSERT INTO forex_rate ("CurrencyCode", "RateToTzs", "EffectiveFrom", "CreatedBy", "Notes")
SELECT v."CurrencyCode", v."RateToTzs", NOW(), 'system_seed', v."Notes"
FROM (VALUES
    ('USD', 2700.0::numeric, 'Placeholder; update with current Bank of Tanzania rate'),
    ('AED', 735.0::numeric,  'Placeholder; ~USD/3.67'),
    ('GBP', 3450.0::numeric, 'Placeholder'),
    ('EUR', 2950.0::numeric, 'Placeholder')
) AS v("CurrencyCode","RateToTzs","Notes")
WHERE NOT EXISTS (SELECT 1 FROM forex_rate fr WHERE fr."CurrencyCode" = v."CurrencyCode");

-- 4.2 sku_counters ----------------------------------------------------------
CREATE TABLE IF NOT EXISTS sku_counters (
    "Prefix"        VARCHAR(8) PRIMARY KEY,
    "CurrentValue"  BIGINT NOT NULL,
    "LastUpdated"   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Seed from current SAP MAX per prefix BEFORE go-live:
--   MAX(CAST(SUBSTRING(ItemCode, len(prefix)+1) AS BIGINT)) WHERE ItemCode LIKE 'PREFIX%'
INSERT INTO sku_counters ("Prefix", "CurrentValue")
SELECT v."Prefix", v."CurrentValue"
FROM (VALUES
    ('BM',  100000::bigint),
    ('VAG', 10000::bigint),
    ('MB',  100000::bigint),
    ('LR',  100600::bigint),
    ('FRD', 10000::bigint),
    ('MIN', 10000::bigint),
    ('VOL', 10000::bigint)
) AS v("Prefix","CurrentValue")
WHERE NOT EXISTS (SELECT 1 FROM sku_counters sc WHERE sc."Prefix" = v."Prefix");

-- 4.3 pricing_brand_ratios --------------------------------------------------
CREATE TABLE IF NOT EXISTS pricing_brand_ratios (
    "Id"             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "Brand"          VARCHAR(50) NOT NULL,
    "PriceBandMin"   NUMERIC(18, 2) NOT NULL,       -- TZS lower bound (inclusive)
    "PriceBandMax"   NUMERIC(18, 2) NULL,           -- NULL = unbounded
    "Pl01ToPl03"     NUMERIC(6, 4) NOT NULL,        -- e.g. 0.85 = PL03 is 85% of PL01
    "EffectiveFrom"  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    "EffectiveTo"    TIMESTAMPTZ NULL,
    "CreatedBy"      VARCHAR(100) NOT NULL,
    "Notes"          TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_pricing_brand_ratios_lookup
    ON pricing_brand_ratios ("Brand", "PriceBandMin")
    WHERE "EffectiveTo" IS NULL;

INSERT INTO pricing_brand_ratios ("Brand", "PriceBandMin", "PriceBandMax", "Pl01ToPl03", "CreatedBy", "Notes")
SELECT v."Brand", v."PriceBandMin", v."PriceBandMax", v."Pl01ToPl03", 'system_seed', v."Notes"
FROM (VALUES
    ('VAG', 0::numeric,      50000::numeric, 0.88::numeric, 'Low-band VAG'),
    ('VAG', 50000::numeric,  NULL::numeric,  0.85::numeric, 'High-band VAG'),
    ('BMW', 0::numeric,      50000::numeric, 0.87::numeric, NULL),
    ('BMW', 50000::numeric,  NULL::numeric,  0.84::numeric, NULL),
    ('MB',  0::numeric,      50000::numeric, 0.86::numeric, NULL),
    ('MB',  50000::numeric,  NULL::numeric,  0.83::numeric, NULL),
    ('LR',  0::numeric,      100000::numeric,0.85::numeric, 'Borrowed; low-band'),
    ('LR',  100000::numeric, NULL::numeric,  0.82::numeric, 'Borrowed; high-band')
) AS v("Brand","PriceBandMin","PriceBandMax","Pl01ToPl03","Notes")
WHERE NOT EXISTS (
    SELECT 1 FROM pricing_brand_ratios p
    WHERE p."Brand" = v."Brand" AND p."PriceBandMin" = v."PriceBandMin" AND p."EffectiveTo" IS NULL
);

-- 4.4 staging_document_line audit columns -----------------------------------
ALTER TABLE staging_document_line
    ADD COLUMN IF NOT EXISTS "EnrichmentSource"      VARCHAR(50) NULL,
    ADD COLUMN IF NOT EXISTS "BorrowedFromArticle"   VARCHAR(50) NULL,
    ADD COLUMN IF NOT EXISTS "BorrowedFromSupplier"  VARCHAR(50) NULL,
    ADD COLUMN IF NOT EXISTS "EnrichmentConfirmedBy" VARCHAR(100) NULL,
    ADD COLUMN IF NOT EXISTS "EnrichmentConfirmedAt" TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS "GeneratedItemCode"     VARCHAR(50) NULL,
    ADD COLUMN IF NOT EXISTS "Pl01Tzs"               NUMERIC(18, 2) NULL,
    ADD COLUMN IF NOT EXISTS "Pl03Tzs"               NUMERIC(18, 2) NULL,
    ADD COLUMN IF NOT EXISTS "Pl05Tzs"               NUMERIC(18, 2) NULL,
    ADD COLUMN IF NOT EXISTS "ForexRateUsed"         NUMERIC(18, 6) NULL,
    ADD COLUMN IF NOT EXISTS "WrittenToSapAt"        TIMESTAMPTZ NULL;

COMMIT;
