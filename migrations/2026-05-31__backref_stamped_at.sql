-- Item Provisioning: track which NeonProducts rows have had their Odoo product id
-- stamped back onto the SAP item UDF, so the OdooBackrefWorker doesn't reprocess them
-- and doesn't need to query SAP each cycle.
--
-- Run on the Neon (PostgreSQL) database before deploying the Item Provisioning code.

ALTER TABLE public."NeonProducts"
ADD COLUMN IF NOT EXISTS "BackrefStampedAt" timestamptz NULL;
