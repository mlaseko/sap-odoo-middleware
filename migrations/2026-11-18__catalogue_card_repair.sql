-- catalogue_card_repair: the record of the one-time repair of catalogue cards (oitm rows) that carry a
-- SAP item code but describe another part (their article number differs from the SAP item's).
--
-- One row per repaired item. action:
--   'linked'  — our item code was taken off wrong_oitm_id and put on correct_oitm_id (an existing card
--               of the item's real article that had no item code);
--   'created' — our item code was taken off wrong_oitm_id and a new card new_oitm_id was made for the
--               item's real article (source 'molas_card_repair', with the OEM numbers from its SAP name).
-- undone_at is set when POST /api/autohub/catalogue-card-repair/undo puts a run back.
-- Additive and idempotent. Run against the Autohub Neon (Parts_Catalog) database before using the repair.

BEGIN;

CREATE TABLE IF NOT EXISTS public.catalogue_card_repair (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_id          UUID          NOT NULL,
    item_code       VARCHAR(50)   NOT NULL,
    action          VARCHAR(20)   NOT NULL,
    wrong_oitm_id   BIGINT        NOT NULL,
    correct_oitm_id BIGINT        NULL,
    new_oitm_id     BIGINT        NULL,
    sap_article     VARCHAR(100)  NOT NULL,
    wrong_article   VARCHAR(100)  NULL,
    oem_count       INTEGER       NOT NULL DEFAULT 0,
    done_by         VARCHAR(100)  NOT NULL,
    done_at         TIMESTAMPTZ   NOT NULL DEFAULT now(),
    undone_at       TIMESTAMPTZ   NULL
);

CREATE INDEX IF NOT EXISTS ix_catalogue_card_repair_run ON public.catalogue_card_repair (run_id);

-- GRANT SELECT, INSERT, UPDATE ON public.catalogue_card_repair TO <app_role>;

COMMIT;
