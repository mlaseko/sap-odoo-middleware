-- invoice_activity — the history of an Autohub invoice: who did what, when, to which line.
--
-- One row per change: a line-level change carries line_id (and the line number at the time, so the
-- history still reads after re-extraction replaces the lines); an invoice-level change (bulk action,
-- Bulk Create run, review completed) has line_id NULL. Written by the middleware after each successful
-- action (best effort: a failed write never fails the user's action). Append-only.
-- Deleted with its invoice. Additive and idempotent. Run against the Autohub Neon (Parts_Catalog) database.

BEGIN;

CREATE TABLE IF NOT EXISTS public.invoice_activity (
    id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_id  UUID          NOT NULL REFERENCES public."staging_document"("Id") ON DELETE CASCADE,
    line_id      UUID          NULL,
    line_number  INTEGER       NULL,
    action       VARCHAR(40)   NOT NULL,
    detail       TEXT          NOT NULL,
    actor        VARCHAR(100)  NOT NULL,
    at           TIMESTAMPTZ   NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_invoice_activity_document ON public.invoice_activity (document_id, at DESC);
CREATE INDEX IF NOT EXISTS ix_invoice_activity_line     ON public.invoice_activity (line_id, at DESC) WHERE line_id IS NOT NULL;

-- GRANT SELECT, INSERT ON public.invoice_activity TO <app_role>;

COMMIT;
