-- pricing_overrides — the audit trail behind the price review gate.
--
-- Every reviewer decision on POST /api/sap/items (a corrected CIF and/or a PL03 set
-- directly) lands here with the formula's answer next to the applied one, so the next
-- ratio review is "SELECT brand, AVG(pl03_difference) ..." instead of reconstructing
-- history from SAP. Append-only; the middleware never reads it back.
--
-- source ∈ 'cif_override' | 'pl03_override' | 'cif+pl03_override'.
-- formula_* are NULL when no CIF was supplied (formula unknown at override time).
--
-- Run against the Autohub Neon (parts_catalog) database. If the app role differs from
-- the migration role, uncomment the GRANT.

BEGIN;

CREATE TABLE IF NOT EXISTS pricing_overrides (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    item_code       VARCHAR(50)   NOT NULL,
    brand           VARCHAR(100)  NULL,
    cif             NUMERIC(18,2) NULL,            -- CIF used (the corrected one when cif_override)
    formula_pl01    NUMERIC(18,2) NULL,            -- what the engine would have produced
    formula_pl03    NUMERIC(18,2) NULL,
    formula_pl05    NUMERIC(18,2) NULL,
    applied_pl01    NUMERIC(18,2) NULL,            -- what was actually written to SAP
    applied_pl03    NUMERIC(18,2) NULL,
    applied_pl05    NUMERIC(18,2) NULL,
    pl03_difference NUMERIC(18,2) NULL,            -- applied_pl03 - formula_pl03 (NULL when formula unknown)
    source          VARCHAR(30)   NOT NULL,
    reason          TEXT          NULL,
    requested_by    VARCHAR(100)  NULL,
    created_at      TIMESTAMPTZ   NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_pricing_overrides_item ON pricing_overrides (item_code);
CREATE INDEX IF NOT EXISTS ix_pricing_overrides_brand_created
    ON pricing_overrides (brand, created_at);

-- GRANT SELECT, INSERT ON pricing_overrides TO <app_role>;

COMMIT;
