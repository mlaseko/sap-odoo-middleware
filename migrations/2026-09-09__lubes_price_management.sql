-- Lubes price management (documentation copy — the middleware applies this
-- automatically and idempotently at runtime via LubesPricingRepository).

-- Runtime-editable settings (EUR→TZS rate lives here under Key = 'EurTzsRate';
-- when absent, Pricing:EurTzsRate from appsettings is the fallback).
CREATE TABLE IF NOT EXISTS public."pricing_settings" (
    "Key"       text PRIMARY KEY,
    "Value"     text NOT NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);

-- Price-change audit trail (all prices NET TZS).
CREATE TABLE IF NOT EXISTS public."price_change_log" (
    "Id"              bigserial PRIMARY KEY,
    "ItemCode"        text NOT NULL,
    "OldPl1"          numeric NULL, "OldPl2" numeric NULL,
    "OldPl3"          numeric NULL, "OldPl4" numeric NULL,
    "NewPl1"          numeric NOT NULL, "NewPl2" numeric NOT NULL,
    "NewPl3"          numeric NOT NULL, "NewPl4" numeric NOT NULL,
    "EurCost"         numeric NULL,
    "Rate"            numeric NULL,
    "PricingCategory" text NULL,
    "Mode"            text NOT NULL,       -- manual | bulk | (future: auto)
    "Note"            text NULL,
    "ChangedAt"       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "ix_price_change_log_item"
    ON public."price_change_log" ("ItemCode", "ChangedAt" DESC);

-- The inputs an item was last priced with (enables reprice-at-new-rate and drift checks).
ALTER TABLE public."NeonProducts"
    ADD COLUMN IF NOT EXISTS "LastEurCost"    numeric NULL,
    ADD COLUMN IF NOT EXISTS "LastEurTzsRate" numeric NULL,
    ADD COLUMN IF NOT EXISTS "LastPricedAt"   timestamptz NULL;
