-- Phase B Slice 2.1 — BACKFILL: split mis-supplier'd oitm rows into own-identity rows.
--
-- WHY: before the Slice 2.1 routing fix, cross-supplier classification was skipped for donors
-- whose item_code was NULL (the normal fresh Path E / RapidAPI state). Tantivy invoices for
-- 'vika' therefore minted SAP item_codes directly onto donor rows belonging to TecDoc suppliers
-- (VAICO / AKS DASIS / ESEN SKV / FREY / EPS / ORIGINAL Schäferbart / OPTIMAL / 1A FIRST
-- AUTOMOTIVE / PartsTec). Each such row now has supplier_name <> the invoice brand, and a
-- future invoice for the same OEMs under the same brand would not match it.
--
-- WHAT THIS DOES, per affected donor row:
--   1) FIRST clear donor's item_code (releases the unique constraint slot on idx_oitm_item_code)
--   2) Mint a NEW own-identity oitm row  → supplier_name = invoice brand,
--                                          source = 'molas_<src>_cross_supplier',
--                                          item_code = the minted SAP code,
--                                          article_number / canonical_oem_number copied from donor
--   3) Copy the donor's OEM cross-references onto the new row
--   4) Repoint the staging line          → NeonOitmId = new row, so the line agrees with its SAP item
--                                          and MatchStrategy upgrades to *_cross_supplier_create_new
--
-- SAFETY: WHERE clause is generic — finds all drifted rows by the (line.Brand != oitm.supplier_name)
-- condition, scoped to DGX-created donors (rapidapi_tecdoc_live / germax_local). No hardcoded IDs.
--
-- The transaction ends in COMMIT. To run as a dry-run first, change the final COMMIT to ROLLBACK.

BEGIN;

DO $$
DECLARE
    rec     RECORD;
    new_id  BIGINT;
    n_done  INT := 0;
BEGIN
    FOR rec IN
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
        WHERE o.item_code IS NOT NULL
          AND o.source IN ('rapidapi_tecdoc_live', 'germax_local')   -- only DGX-created donors
          AND l."Brand" IS NOT NULL
          AND l."Brand" != ''
          AND lower(btrim(l."Brand")) <> lower(btrim(o.supplier_name))   -- true cross-supplier drift
        ORDER BY o.id
    LOOP
        -- 1) FIRST clear the donor's item_code (releases the unique constraint slot).
        --    This MUST happen BEFORE the INSERT below — otherwise idx_oitm_item_code
        --    rejects the new row as a duplicate of the donor.
        UPDATE oitm SET item_code = NULL WHERE id = rec.donor_id;

        -- 2) Now mint the own-identity row under the invoice brand (no conflict).
        INSERT INTO oitm (article_number, supplier_name, canonical_oem_number,
                          item_code, tecdoc_article_id, source)
        VALUES (rec.article_number,
                rec.brand,
                rec.canonical_oem_number,
                rec.minted_code,
                NULL,
                CASE WHEN rec.donor_source = 'germax_local'
                     THEN 'molas_germax_cross_supplier'
                     ELSE 'molas_rapidapi_cross_supplier' END)
        RETURNING id INTO new_id;

        -- 3) Carry the donor's OEM cross-references onto the new row.
        INSERT INTO oitm_cross_reference (oitm_id, oem_number, reference_type)
        SELECT new_id, oem_number, reference_type
        FROM oitm_cross_reference
        WHERE oitm_id = rec.donor_id AND reference_type = 'oem';

        -- 4) Repoint the staging line at its real SAP item; upgrade the strategy label
        --    so future reads classify it correctly as cross-supplier.
        UPDATE staging_document_line
        SET "NeonOitmId" = new_id,
            "MatchStrategy" = CASE
                WHEN "MatchStrategy" = 'rapidapi_tecdoc_live_create_new'
                    THEN 'rapidapi_cross_supplier_create_new'
                WHEN "MatchStrategy" = 'germax_local_create_new'
                    THEN 'germax_cross_supplier_create_new'
                ELSE "MatchStrategy"
            END
        WHERE "Id" = rec.line_id;

        n_done := n_done + 1;
        RAISE NOTICE 'donor % (% / %) -> new row % (brand=%, item_code=%)',
            rec.donor_id, rec.donor_supplier, rec.minted_code,
            new_id, rec.brand, rec.minted_code;
    END LOOP;

    RAISE NOTICE 'Slice 2.1 backfill: % donor row(s) split into own-identity rows.', n_done;
END $$;

-- Verification (inside the transaction so you see the would-be post-state).
-- Expect donors with TecDoc suppliers to show item_code=NULL, and new molas_*_cross_supplier
-- rows under the invoice brand with the SAP codes that were minted.
SELECT 'donors (expect item_code=NULL):' AS label;
SELECT id, item_code, supplier_name, source
FROM oitm
WHERE source IN ('rapidapi_tecdoc_live', 'germax_local')
  AND create_date > NOW() - INTERVAL '24 hours'
ORDER BY id;

SELECT 'new own-identity rows (expect supplier=invoice brand):' AS label;
SELECT id, item_code, supplier_name, source, create_date
FROM oitm
WHERE source LIKE 'molas_%cross_supplier%'
ORDER BY id;

COMMIT;
-- For dry-run mode, change COMMIT above to ROLLBACK and re-run.