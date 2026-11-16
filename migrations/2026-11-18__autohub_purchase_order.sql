-- Autohub purchase order from a reviewed invoice.
--
-- autohub_supplier_vendor: the SAP vendor (OCRD CardCode) the operator picked for an invoice supplier name,
--   remembered so the next invoice of that supplier pre-selects it. Keyed by the normalised supplier name
--   (trimmed, upper case).
-- autohub_purchase_order: the SAP purchase order created from an invoice (one per invoice). The PO also
--   carries the invoice id in OPOR.U_AppRef and the invoice number in OPOR.NumAtCard.
--
-- Additive and idempotent. Run against the Autohub Neon (Parts_Catalog) database.

BEGIN;

CREATE TABLE IF NOT EXISTS public.autohub_supplier_vendor (
    supplier_key  VARCHAR(200)  PRIMARY KEY,
    supplier_name VARCHAR(200)  NOT NULL,
    card_code     VARCHAR(15)   NOT NULL,
    card_name     VARCHAR(100)  NULL,
    updated_by    VARCHAR(100)  NOT NULL,
    updated_at    TIMESTAMPTZ   NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS public.autohub_purchase_order (
    document_id   UUID          PRIMARY KEY REFERENCES public."staging_document"("Id") ON DELETE CASCADE,
    card_code     VARCHAR(15)   NOT NULL,
    doc_entry     INTEGER       NOT NULL,
    doc_num       INTEGER       NOT NULL,
    line_count    INTEGER       NOT NULL,
    doc_total     NUMERIC(19,2) NOT NULL,
    created_by    VARCHAR(100)  NOT NULL,
    created_at    TIMESTAMPTZ   NOT NULL DEFAULT now()
);

-- GRANT SELECT, INSERT, UPDATE ON public.autohub_supplier_vendor, public.autohub_purchase_order TO <app_role>;

COMMIT;
