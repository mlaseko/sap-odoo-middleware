-- Autohub Phase A — staging tables for spare-parts invoice extraction.
-- Run this against the parts_catalog Neon branch (NOT MolasLUBES).
-- Additive and idempotent (CREATE TABLE/INDEX IF NOT EXISTS); safe to re-run and safe on rollback.
--
-- Phase B-reserved columns (review/match/create/pricing/enrichment + document forex) are created
-- now so Phase B needs no further migration on these tables. They sit null/default until then.

BEGIN;

CREATE TABLE IF NOT EXISTS public."staging_document" (
    "Id"                     uuid          PRIMARY KEY,
    "CompanyKey"             text          NOT NULL DEFAULT 'Autohub',
    "OriginalFilename"       text          NOT NULL,
    "FilePath"               text          NOT NULL,
    "FileSha256"             text          NOT NULL,
    "PageCount"              integer       NOT NULL DEFAULT 0,
    "Status"                 text          NOT NULL DEFAULT 'uploaded',
    "ValidationStatus"       text          NULL,
    "ErrorMessage"           text          NULL,
    "RawExtractionJson"      jsonb         NULL,
    "UploadedAt"             timestamptz   NOT NULL DEFAULT NOW(),
    "ExtractedAt"            timestamptz   NULL,
    -- Live progress (mirrors the Lubes hardening PR)
    "PagesProcessed"         integer       NOT NULL DEFAULT 0,
    "CurrentPageStartedAt"   timestamptz   NULL,
    "LastPageDurationSec"    numeric(8,2)  NULL,
    -- Header fields extracted from invoice
    "SupplierName"           text          NULL,
    "InvoiceNumber"          text          NULL,
    "InvoiceDate"            date          NULL,
    "Currency"               text          NULL,
    "TotalAmount"            numeric(14,2) NULL,
    -- Reserved for Phase B (do not populate yet)
    "ReviewedAt"             timestamptz   NULL,
    "ReviewedBy"             text          NULL,
    "AutoMatchedAt"          timestamptz   NULL,
    "AutoMatchedCount"       integer       NOT NULL DEFAULT 0,
    "ForexRateUsed"          numeric(14,6) NULL,
    "ForexRateDate"          date          NULL
);

CREATE INDEX IF NOT EXISTS ix_staging_document_status_uploadedat
    ON public."staging_document" ("Status", "UploadedAt" DESC);
CREATE INDEX IF NOT EXISTS ix_staging_document_filesha256
    ON public."staging_document" ("FileSha256");

CREATE TABLE IF NOT EXISTS public."staging_document_line" (
    "Id"                     uuid          PRIMARY KEY,
    "DocumentId"             uuid          NOT NULL REFERENCES public."staging_document"("Id") ON DELETE CASCADE,
    "CompanyKey"             text          NOT NULL DEFAULT 'Autohub',
    "LineNumber"             integer       NOT NULL,
    "PageNumber"             integer       NULL,
    -- Parts-specific fields
    "SupplierArticleNumber"  text          NULL,
    "OemNumbers"             jsonb         NULL,         -- array of strings, e.g. ["11538650983","11538631943"]
    "Description"            text          NULL,
    "Brand"                  text          NULL,
    "Quantity"               numeric(14,4) NULL,
    "Unit"                   text          NULL,
    "UnitPriceForeign"       numeric(14,4) NULL,         -- invoice currency, NOT TZS
    "DiscountPct"            numeric(8,4)  NULL,
    "LineTotalForeign"       numeric(14,2) NULL,
    "IsPromotional"          boolean       NOT NULL DEFAULT false,
    -- Reserved for Phase B (review/match/create)
    "ReviewStatus"           text          NOT NULL DEFAULT 'pending',
    "MatchedOitmId"          integer       NULL,
    "MatchedItemCode"        text          NULL,
    "CreatedSku"             text          NULL,
    "CreatedAt"              timestamptz   NULL,
    "CreateErrorMessage"     text          NULL,
    "EditedAt"               timestamptz   NULL,
    "EditedBy"               text          NULL,
    -- Pricing fields, populated in Phase B
    "UnitPriceTzs"           numeric(14,2) NULL,
    "BuyingPriceTzs"         numeric(14,2) NULL,
    "PriceList01"            numeric(14,2) NULL,
    "PriceList03"            numeric(14,2) NULL,
    "PriceList05"            numeric(14,2) NULL,
    -- Enrichment results, populated in Phase B
    "EnrichmentStatus"       text          NULL,         -- 'enriched_direct' | 'enriched_borrowed' | 'not_found' | 'failed'
    "EnrichmentOitmId"       integer       NULL,
    "EnrichmentSummary"      jsonb         NULL
);

CREATE INDEX IF NOT EXISTS ix_staging_document_line_documentid_linenumber
    ON public."staging_document_line" ("DocumentId", "LineNumber");
CREATE INDEX IF NOT EXISTS ix_staging_document_line_reviewstatus
    ON public."staging_document_line" ("DocumentId", "ReviewStatus");

-- Grant minimum needed permissions to the middleware role.
-- NOTE: adjust 'sap_odoo_role' to whatever role parts_catalog actually uses for the middleware.
GRANT SELECT, INSERT, UPDATE, DELETE
    ON public."staging_document", public."staging_document_line"
    TO sap_odoo_role;

COMMIT;
