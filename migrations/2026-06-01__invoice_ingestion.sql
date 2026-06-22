-- Phase A: invoice ingestion staging tables
-- One row per uploaded PDF; one row per extracted line item.
-- Run on the Neon (PostgreSQL) database before deploying the invoice-ingestion code.

CREATE TABLE IF NOT EXISTS public."staging_document" (
    "Id"                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "OriginalFilename"  text NOT NULL,
    "FilePath"          text NOT NULL,
    "FileHash"          text NOT NULL,
    "FileSizeBytes"     bigint NOT NULL,
    "PageCount"         integer,
    "Status"            text NOT NULL DEFAULT 'uploaded'
                          CHECK ("Status" IN ('uploaded','extracting','extracted','failed')),
    "DocumentType"      text NOT NULL DEFAULT 'invoice',
    "Supplier"          text DEFAULT 'LIQUI MOLY GmbH',
    "InvoiceNumber"     text,
    "InvoiceDate"       date,
    "SalesOrder"        text,
    "DeliveryNoteRef"   text,
    "CustomerName"      text,
    "CustomerAccount"   text,
    "Currency"          text,
    "Subtotal"          numeric(18,4),
    "Freight"           numeric(18,4),
    "TotalNet"          numeric(18,4),
    "TaxAmount"         numeric(18,4),
    "InvoiceTotal"      numeric(18,4),
    "PaymentTerms"      text,
    "DueDate"           date,
    "ValidationStatus"  text,
    "ValidationNotes"   text,
    "RawExtractionJson" jsonb,
    "ErrorMessage"      text,
    "UploadedAt"        timestamptz NOT NULL DEFAULT now(),
    "ExtractedAt"       timestamptz
);

-- Deduplicate by file hash (re-uploading the same PDF surfaces the existing row).
CREATE UNIQUE INDEX IF NOT EXISTS uq_staging_document_hash
    ON public."staging_document"("FileHash");

CREATE INDEX IF NOT EXISTS ix_staging_document_status
    ON public."staging_document"("Status");

CREATE TABLE IF NOT EXISTS public."staging_document_line" (
    "Id"             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "DocumentId"     uuid NOT NULL REFERENCES public."staging_document"("Id") ON DELETE CASCADE,
    "LineNo"         integer NOT NULL,
    "PageNo"         integer NOT NULL,
    "ArticleNumber"  text,
    "Description"    text,
    "PackSize"       text,
    "UnitPrice"      numeric(18,4),
    "Quantity"       numeric(18,4),
    "Unit"           text,
    "CommodityCode"  text,
    "Origin"         text,
    "DiscountPct"    numeric(5,2) DEFAULT 0,
    "LineTotal"      numeric(18,4),
    "IsPromotional"  boolean DEFAULT false,
    "CreatedAt"      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_staging_document_line_document
    ON public."staging_document_line"("DocumentId");

-- Grant the application role access (Phase A is read+write for the middleware).
GRANT SELECT, INSERT, UPDATE, DELETE ON public."staging_document"      TO sap_odoo_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON public."staging_document_line" TO sap_odoo_role;
