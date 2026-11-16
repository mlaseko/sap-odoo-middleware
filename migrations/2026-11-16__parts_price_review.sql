-- parts_price_review — the reviewer's price decision per Autohub invoice line.
--
-- One row per staging_document_line. The Price review step on the Autohub document page reads
-- the engine's prices for every line still to be created, the reviewer approves or holds each
-- line, and may set a different PL03 (PL05 is then re-derived from it). Bulk Create will later
-- create only approved lines, at final_pl03.
--
-- The row also keeps the inputs the decision was based on (unit price, discount, currency,
-- forex rate, CIF, formula PL03). If any of them differs when the review is read again, the
-- decision is shown as stale and must be approved again — nobody creates an item from a price
-- that was approved for different figures.
--
-- Lines are deleted with their document (re-extraction replaces lines), so decisions go too.
-- Additive and idempotent. Run against the Autohub Neon (Parts_Catalog) database.

BEGIN;

CREATE TABLE IF NOT EXISTS public.parts_price_review (
    line_id            UUID          PRIMARY KEY
                       REFERENCES public."staging_document_line"("Id") ON DELETE CASCADE,
    document_id        UUID          NOT NULL,
    status             VARCHAR(20)   NOT NULL CHECK (status IN ('approved', 'held')),

    -- Inputs the decision was based on (stale check).
    unit_price_foreign NUMERIC(18,4) NULL,
    discount_pct       NUMERIC(9,4)  NULL,
    currency           VARCHAR(10)   NULL,
    forex_rate         NUMERIC(18,6) NULL,
    cif_tzs            NUMERIC(18,2) NULL,

    -- Engine output at decision time.
    pl01               NUMERIC(18,2) NULL,
    formula_pl03       NUMERIC(18,2) NULL,
    formula_pl05       NUMERIC(18,2) NULL,

    -- Reviewer's decision. final_pl03 = formula_pl03 when the price was accepted unchanged.
    final_pl03         NUMERIC(18,2) NULL,
    final_pl05         NUMERIC(18,2) NULL,
    reason             TEXT          NULL,

    reviewed_by        VARCHAR(100)  NOT NULL,
    reviewed_at        TIMESTAMPTZ   NOT NULL DEFAULT now(),

    -- A changed selling price must say why.
    CONSTRAINT ck_parts_price_review_reason
        CHECK (final_pl03 IS NULL OR final_pl03 = formula_pl03 OR NULLIF(btrim(reason), '') IS NOT NULL)
);

CREATE INDEX IF NOT EXISTS ix_parts_price_review_document
    ON public.parts_price_review (document_id);

-- GRANT SELECT, INSERT, UPDATE, DELETE ON public.parts_price_review TO <app_role>;

COMMIT;
