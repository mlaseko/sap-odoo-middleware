-- Lubes price management schema.
--
-- The middleware TRIES to apply this automatically at runtime, but on Neon the
-- app login usually has no CREATE right on schema public (Postgres 15+ default),
-- which surfaces as: "42501: permission denied for schema public".
-- In that case run this WHOLE FILE ONCE in the Neon SQL editor as the database
-- OWNER role, then the middleware heals automatically on its next request.
--
-- >>> Replace <app_user> in the GRANT block at the bottom with the username from
-- >>> the middleware's Neon:ConnectionString.

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

-- Runtime ratio overrides (the /pricing/ratios editor). Defaults stay in code;
-- rows here override per category+band and load at startup.
CREATE TABLE IF NOT EXISTS public."pricing_band_ratio_overrides" (
    "Category"  text NOT NULL,
    "Band"      text NOT NULL,
    "SpRatio"     numeric NOT NULL,
    "DealerRatio" numeric NOT NULL,
    "RetailRatio" numeric NOT NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("Category", "Band")
);
CREATE TABLE IF NOT EXISTS public."pricing_maasai_ratio_overrides" (
    "Category"  text NOT NULL,
    "BandIndex" int  NOT NULL,
    "Ratio"     numeric NOT NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("Category", "BandIndex")
);
CREATE TABLE IF NOT EXISTS public."ratio_change_log" (
    "Id"        bigserial PRIMARY KEY,
    "Kind"      text NOT NULL,       -- band | maasai | reset
    "Category"  text NOT NULL,
    "Band"      text NOT NULL,
    "NewValues" text NOT NULL,
    "Note"      text NULL,
    "ChangedAt" timestamptz NOT NULL DEFAULT now()
);

-- ── Grants for the middleware's app login ─────────────────────────────
-- Replace <app_user> with the username from Neon:ConnectionString.
GRANT SELECT, INSERT, UPDATE, DELETE ON
    public."pricing_settings",
    public."price_change_log",
    public."pricing_band_ratio_overrides",
    public."pricing_maasai_ratio_overrides",
    public."ratio_change_log"
TO <app_user>;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO <app_user>;

-- Optional (future-proof): let the app create its own tables next time, so new
-- middleware versions never need a manual migration again:
-- GRANT CREATE ON SCHEMA public TO <app_user>;
