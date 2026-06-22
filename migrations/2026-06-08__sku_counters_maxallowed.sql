-- SKU counter refresh: deterministic per-prefix ceiling instead of LAG gap detection.
--
-- Real OITM data defeats any single gap threshold: MB test outliers sit at gap=1496, VAG outliers at
-- gap=4446, yet VAG also has a LEGITIMATE internal gap of 1228. No one threshold separates outliers
-- from real items. Instead each prefix gets an explicit ceiling — SapSkuCounterRefreshService takes
-- MAX(suffix) WHERE suffix <= MaxAllowed, so test items parked above the ceiling never inflate the
-- counter. When such items become legitimate, the operator just raises MaxAllowed. NULL = no ceiling.
--
-- The refresh also logs how many items currently sit above each prefix's ceiling so the operator
-- knows when to bump it.

ALTER TABLE sku_counters ADD COLUMN IF NOT EXISTS "MaxAllowed" BIGINT NULL;

-- Seed ceilings per prefix here once the operator confirms the cut-off for each, e.g.:
--   UPDATE sku_counters SET "MaxAllowed" = 19999 WHERE "Prefix" = 'VAG';
--   UPDATE sku_counters SET "MaxAllowed" = 199999 WHERE "Prefix" = 'MB';
-- Left NULL by default (no ceiling) so the refresh is a pure MAX until ceilings are set.
