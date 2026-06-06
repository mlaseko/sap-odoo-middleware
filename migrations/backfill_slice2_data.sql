-- Phase B Slice 2.1 — BACKFILL: split the 8 mis-supplier'd oitm rows into own-identity rows.
--
-- WHY: before the Slice 2.1 routing fix, cross-supplier classification was skipped for donors whose
-- item_code was NULL (the normal fresh Path E / RapidAPI state). The Tantivy invoice (2025NS0666,
-- brand 'vika') therefore minted SAP item_codes directly onto donor rows belonging to TecDoc suppliers
-- (VAICO / AKS DASIS / ESEN SKV / FREY / EPS / ORIGINAL Schäferbart / OPTIMAL). Each such row now has
-- supplier_name <> the invoice brand, and a future 'vika' invoice for the same OEMs would not match it.
--
-- WHAT THIS DOES, per affected donor row:
--   1) mint a NEW own-identity oitm row  → supplier_name = invoice brand ('vika'),
--                                           source = 'molas_<src>_cross_supplier',
--                                           item_code = the minted SAP code,
--                                           article_number / canonical_oem_number copied from the donor;
--   2) copy the donor's OEM cross-references onto the new row;
--   3) reset the donor row              → item_code = NULL (TecDoc identity restored);
--   4) repoint the staging line          → NeonOitmId = new row, so the line agrees with its SAP item.
--
-- SAFETY: this script is a DRY RUN — it ends in ROLLBACK and prints (via NOTICE) exactly what it would
-- change plus a verification SELECT. Review that output, then change the final ROLLBACK to COMMIT and
-- re-run. The donor id allowlist below is the authoritative target set (from the production smoke);
-- the per-row transform is derived generically from each donor + its staging line.
--
-- Apply order (per spec): deploy Slice 2.1 FIRST, then run this with COMMIT, then re-verify.

BEGIN;

DO $$
DECLARE
    rec     RECORD;
    new_id  BIGINT;
    n_done  INT := 0;
BEGIN
    -- Materialise the target set BEFORE mutating, so the loop never scans oitm while we change it.
    CREATE TEMP TABLE _slice21_targets ON COMMIT DROP AS
        SELECT o.id            AS donor_id,
               o.item_code     AS minted_code,
               o.article_number,
               o.canonical_oem_number,
               o.source        AS donor_source,
               o.supplier_name AS donor_supplier,
               l."Id"          AS line_id,
               l."Brand"       AS brand
        FROM oitm o
        JOIN staging_document_line l
          ON l."NeonOitmId" = o.id
         AND l."MatchedItemCode" = o.item_code
        WHERE o.id IN (10186, 10187, 10189, 10190, 10191, 10193, 10194, 10195)
          AND o.item_code IS NOT NULL
          AND l."Brand" IS NOT NULL
          AND lower(btrim(l."Brand")) <> lower(btrim(o.supplier_name));   -- guard: only true drift

    FOR rec IN SELECT * FROM _slice21_targets LOOP
        -- 1) free the donor's item_code FIRST. There is a unique index on oitm.item_code, so the new
        --    own-identity row cannot carry the same code while the donor still holds it. rec already
        --    captured the donor's values, so nulling it now is safe. (The index allows many NULLs.)
        UPDATE oitm SET item_code = NULL WHERE id = rec.donor_id;

        -- 2) mint the own-identity row under our brand, now holding the minted code
        INSERT INTO oitm (article_number, supplier_name, canonical_oem_number, item_code, tecdoc_article_id, source)
        VALUES (rec.article_number,
                rec.brand,
                rec.canonical_oem_number,
                rec.minted_code,
                NULL,
                CASE WHEN rec.donor_source = 'germax_local'
                     THEN 'molas_germax_cross_supplier'
                     ELSE 'molas_rapidapi_cross_supplier' END)
        RETURNING id INTO new_id;

        -- 3) carry the donor's OEM cross-references onto the new row
        INSERT INTO oitm_cross_reference (oitm_id, oem_number, reference_type)
        SELECT new_id, oem_number, reference_type
        FROM oitm_cross_reference
        WHERE oitm_id = rec.donor_id AND reference_type = 'oem';

        -- 4) repoint the staging line at its real SAP item
        UPDATE staging_document_line SET "NeonOitmId" = new_id WHERE "Id" = rec.line_id;

        n_done := n_done + 1;
        RAISE NOTICE 'donor % (% / %) -> new row % (% / %), item_code %',
            rec.donor_id, rec.donor_supplier, rec.minted_code,
            new_id, rec.brand, rec.minted_code, rec.minted_code;
    END LOOP;

    RAISE NOTICE 'Slice 2.1 backfill: % donor row(s) split into own-identity rows.', n_done;
END $$;

-- Verification (still inside the transaction so you see the would-be post-state):
SELECT id, item_code, supplier_name, source
FROM oitm
WHERE id IN (10186, 10187, 10189, 10190, 10191, 10193, 10194, 10195)   -- donors: expect item_code NULL, TecDoc supplier
   OR source LIKE 'molas_%cross_supplier%'                              -- new rows: expect supplier='vika'
ORDER BY id;

ROLLBACK;   -- DRY RUN. Review the NOTICE + verification output above, then change to COMMIT and re-run.
