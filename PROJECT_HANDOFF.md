# SAP–Odoo Middleware — Project Handoff

> Living reference for `mlaseko/sap-odoo-middleware`. Written to seed a fresh chat with full context.
> Last updated: 2026-06-16 (after PRs #180–#200).

---

## 1. What this is

A C# **.NET 8 ASP.NET Core** middleware that bridges **SAP Business One (B1)**, **Odoo**, **Neon (Postgres)**, an external **DGX classifier**, and **web scraping** (Liqui Moly / Meguin). It serves a server-rendered **Razor Pages UI** *and* a JSON API.

- **Runs as a Windows Service** named `SapOdooMiddleware` on `WIN-GJGQ73V0C3K`.
- Deployed to **`C:\SapOdoo\SAPMiddleware\`**, listens on **`http://0.0.0.0:5259`**.
- Solution: `src/SapOdooMiddleware/SapOdooMiddleware.csproj`; tests in `tests/`.
- **No CI** — the only build gate is compiling on the Windows box (it needs the SAP `SAPbobsCOM` COM reference).

### Two business domains (tenants): Lubes & Autohub
The same process serves two tenants. **This is the most important distinction.**

| | **Lubes** | **Autohub** |
|---|---|---|
| Product domain | Liqui Moly lubricants (+ **Meguin**, an LM subsidiary) | Automotive spare parts (multi-brand: LR, FRD, MINI, VOL, MB, BM, VAG …) |
| Item source | Web scraping LM/Meguin sites | Supplier data + DGX enrichment (`/enrich`) |
| SKU scheme | LM/Meguin article numbers (e.g. `23139`, `6452`) | Generated `PREFIX+counter` (e.g. `MB101501`) via `SkuCounter` |
| Routes / UI | `/api/documents`, `/api/items`, `Pages/Documents/*` | `/api/autohub/*`, `Areas/Autohub/Pages/Documents/*` |
| Invoice lines table | `staging_document` / `staging_document_line` | `staging_parts_*` |
| Provisioning service | `LubesItemProvisioningService` | `PartsItemProvisioningService` |
| Classifier calls | `/classify` (Odoo cat) + `/classify_family` (SAP group) | `/enrich` (EnrichmentService) |

**Tenant resolution:** `Middleware/TenantResolutionMiddleware` sets the tenant from the URL —
`/autohub` or `/api/autohub` → **Autohub**; **everything else → Lubes** (the default). Backed by
`CompanyContext` (`LubesKey="Lubes"`, `AutohubKey="Autohub"`; unknown keys fall back to Lubes).

**Per-tenant config** comes from `CompanyContext.Build(key)`:
- **Lubes** projects the **top-level** config sections (`SapB1`, `Odoo`, `Neon`, `Classifier`, `DocumentIngestion`).
- **Autohub** uses the **`Companies:Autohub`** config block (its own `SapB1`, `Odoo`, `Neon`, `Classifier`, …).

> ⚠️ **Caveat to verify before Autohub SAP/PO work:** `SapB1DiApiService` injects the **top-level**
> `IOptions<SapB1Settings>` (Lubes) directly — it does **not** read `ICompanyContext`. So the DI-API
> connection currently targets the **Lubes** company DB (`Molas_Lubes_LTD`) regardless of tenant.
> Some services *are* tenant-aware via `ICompanyContext` (e.g. `NeonBridgeService`, the Autohub workers
> which call `CompanyContext.SetCompany(AutohubKey)`), but several repos take top-level `IOptions` directly.
> **The Lubes Purchase Order feature (#194) intentionally relies on this single Lubes connection.** If
> Autohub needs a *separate* SAP company, the SAP service must be made tenant-aware first.

---

## 2. SAP B1 connection & writing

- **`ISapB1Service`** is the contract. **`SapB1DiApiService`** is the real (Windows-only) implementation
  over the **DI API** (`SAPbobsCOM` COM). **`SapB1DiApiServiceStub`** throws `PlatformNotSupportedException`
  for non-Windows builds.
  - 🚨 **Every method added to `ISapB1Service` must also be added to the stub**, or the build breaks
    (this bit us in #196).
- **Connection:** `SapB1Settings` (`Server`, `CompanyDb`, `LicenseServer`, `SLDServer`, `DbServerType`, `UserName`/`Password`). Live values: `Server=WIN-GJGQ73V0C3K`, `CompanyDb=Molas_Lubes_LTD`, `DbType=dst_MSSQL2016`. There's a **DbServerType ordinal fallback** (tries 8 → 10) to survive the `-119 "Database server type not supported"` error.
- **Thread-safety:** the DI API is single-threaded → all calls serialize on a `SemaphoreSlim _lock`.
- **Writing documents** uses `Documents` business objects: `oOrders` (SO), `oInvoices`, `oCreditNotes`, `oDeliveryNotes`, **`oPurchaseOrders` (PO)**. Pattern: set header props → loop lines with `if (i>0) Lines.Add()` → `doc.Add()` → `_company.GetNewObjectKey()` → `GetByKey(docEntry)` → read `DocNum` → `Marshal.ReleaseComObject`.
- **Items** (`oItems`): `CreateLubesItemAsync` / `CreateAutohubItemAsync`. Lubes items use **manual UoM**
  (`UoMGroupEntry=-1`, `InventoryUOM/PurchaseUnit/SalesUnit="Unit"`, `PurchaseItemsPerUnit/SalesItemsPerUnit=1`).
  ⚠️ DI-API property names ≠ OITM column names (e.g. `PurchaseUnit`≠`BuyUnitMsr`, `SalesItemsPerUnit`≠`NumInSale`) — this bit us in #184.
- **Reads** use `Recordset.DoQuery(sql)` — **no parameterization**, so SQL is string-interpolated; escape single quotes / validate inputs. MSSQL dialect, double-quoted identifiers (e.g. `OPOR."NumAtCard"`).
- **Recovery/idempotency:** `GetItemSnapshotAsync` + `UpdateBlankFieldsAsync` → re-POSTing an item "recovers" (fills blank SAP fields) instead of duplicating.

### Purchase Orders (Lubes) — #194
`PurchaseOrderService` + `PurchaseOrdersController`:
- `GET /api/documents/{doc}/purchase-order/preview` → vendor, currency, sellable lines, readiness/blocking.
- `POST /api/documents/{doc}/purchase-order` (body = final lines) → posts to SAP, returns DocEntry/DocNum.
- Rules: vendor `Supplier`→BP via `PurchaseOrders:Vendors` (Liqui Moly → **S00001**; Meguin invoices under same), `DocCurrency`=invoice currency, **`NumAtCard`=invoice number**, **`Comments`="SO: {sales order}"**, line UoM=Unit, warehouse=**MainWHSE**, **no per-line VAT** (inherited from the vendor BP's tax group). Only `matched`/`created` lines; skip/promotional excluded & non-blocking. **Dedup** by querying `OPOR` for an existing `NumAtCard`+`CardCode`.

---

## 3. Neon (Postgres) — the middleware's own DB

Per-tenant connection via `CompanyContext` (Lubes uses top-level `Neon:ConnectionString`; Lubes DB = **`MolasLUBES`** on Neon). Some repos still take top-level `IOptions<NeonSettings>` directly (see tenancy caveat).

Key Lubes tables:
- **`NeonLiquiMolyProducts`** — scraped LM/Meguin **source cache** (Name, Category, SubCategory, Description, PackagingSize, Liter, AllPackagingSizes, ImageUrl/AllImageUrls, PrimaryBarcode + UoM fields, ProductInfoPdfUrl, SafetyDataSheetPdfUrl, **ScrapedAt**). Repo: `NeonLiquiMolyRepository`. Keyed by ArticleNumber.
- **`NeonProducts`** — provisioned items (ItemCode, ItemName, **ItemGroupCode**, **ItemGroupName**, OdooCategoryExternalId/Name, ListPrice, SapStatus, and audit cols **FamilyConfidence / FamilyNeedsReview / FamilyOverrideReason**). Repo: `NeonProductRepository`. JSON serialized **snake_case**.
- **`NeonPriceLists`** — retail/dealer/super-dealer prices.
- **`staging_document` / `staging_document_line`** — invoice ingestion + review state (`ReviewStatus`: pending/matched/created/create_new/create_failed/skip; `MatchedSku`, `CreatedSku`, `IsPromotional`). Repos: `StagingDocumentRepository`, `StagingDocumentLineRepository`.
- Autohub: `staging_parts_*`, `parts_catalog`, sku counters, etc.
- **Migrations** live in `/migrations/*.sql` (dated, additive, idempotent), run manually against Neon.

> The user drives Neon directly with `psql` against `MolasLUBES` for verification/backfills.

---

## 4. Scraping (Lubes: Liqui Moly + Meguin)

`Integrations/LiquiMoly/LiquiMolyProductScraperService` builds a **product index** (SKU → product-page URL)
and scrapes product detail.

- **Index build** = category-page crawl (`CategoryPaths`) **+ sitemap variant-mining** (`SitemapUrls`,
  `UseSitemap`). LM/Meguin **on-site search is broken (HTTP 500)** → resolution is sitemap-driven.
- **Per-brand cache** keyed by `BrandKey`, **persisted to disk** at **`C:\SapOdoo\Cache\*.json`** (outside the
  app folder so redeploys don't wipe it), **23h TTL**. Cold build is bounded by `MineMaxMinutes`; a build
  below `MinIndexSkuCount` isn't cached (anti-poison). The build only caches at the end.
- **Warm-up:** `IndexWarmupHostedService<TScraper,TSettings>` — eager on startup (loads disk cache if fresh,
  else crawls), then refreshes on a 22h timer; retries every `WarmupRetryMinutes` if not warm.
- **Detail scrape** (`ScrapeProductPageForSkuAsync`): name, description, images, **size/Liter from the
  Magento `jsonConfig.gebindeinhalt`** (the breadcrumb/sizes are JS-rendered, so HTML extract is unreliable),
  PDF/SDS from the PIM (`pim.liqui-moly.com/sheets/{sku}`).
- **Category enrichment (#200):** the crawl now records **SKU→category** and sets `dto.Category` (the DGX
  hint). Persisted in the index file (`PersistedIndex.Categories`, parallel `_brandCategories` cache).
  **Without this, `Category` was NULL → DGX had no hint → low-confidence misclassifications.**
- **Meguin** = subclass **`MeguinProductScraperService`** (`BrandKey="Meguin"`, meguin.com, own cache file
  `C:\SapOdoo\Cache\meguin-index.json`, own `CategoryPaths` + sitemap). Same Magento platform → reuses the
  whole pipeline. **Routing:** invoice lines whose name starts with **"Meguin"** are scraped from meguin.com
  (the brand disambiguator — LM/Meguin SKU numbers can collide).
- **Endpoints:** `POST /api/liquimoly/scrape/{sku}`, `POST /api/meguin/scrape/{sku}` (503 "warming" until the
  index is built; upsert into `NeonLiquiMolyProducts`).
- **Known gaps:** barcode/GTIN is **not** published on LM/Meguin pages → needs a dealer EAN export (future
  "barcode importer"). Sitemap-only orphans get no crawl category.

---

## 5. Provisioning & classification (Lubes)

`LubesItemProvisioningService.ProvisionAsync`: **scrape → classify → price → write SAP then Neon**
(SAP is system-of-record; Neon upsert is idempotent). Pipeline:

1. **Ensure LM/Meguin row** (scrape on demand; warns if `ScrapedAt` > `StaleScrapeWarningDays`).
2. **Odoo category** (precedence): **(a)** manual override `OdooCategoryOverride*` → **(b)** Layer-1
   deterministic overrides (coolant, brake fluid → fixed Odoo leaf) → **(c)** DGX `/classify` with the
   `lm.Category` hint. Low confidence → `needs_review` unless `AcceptLowConfidenceCategory`.
3. **SAP family/group** (precedence): **Layer-1 name overrides** (coolant/antifreeze/KFS→**104**,
   brake fluid→**104**, Pro-Line→**112**, motorbike→**110**) → else DGX `/classify_family`; accept
   `needs_review` at **≥ `MinFamilyConfidence` (0.70)** with a WARN (Layer 2).
3. **Pricing** keyed off the **SAP/OITB group** (`PricingCalculator.TryPricingBandForSapGroup`), *not* the
   Odoo category (so siblings price the same). Bands: 104→Service, 105→Engine Oils, 107→Gear Oils, etc.
   (104/107 collapse multiple bands — accepted). EUR→TZS via `Pricing:EurTzsRate`.
4. **Write** SAP item (manual "Unit" UoM) + `NeonProducts` (incl. `ItemGroupName` + audit cols) + prices.

**OITB / SAP item groups (live):** 104 Repair aids/service products · 105 Engine Oils · 106 Commercial
vehicles · 107 Gear Oils/ATF/Greases · 108 Branding · 109 Additives · 110 Motor Bike · 111 Vehicle Care ·
112 Workshop Pro-Line.

### DGX classifier
External service on the **NVIDIA DGX/Spark box** (`molasdgx@spark`). `HttpCategoryClassifier` →
`/classify` (Odoo category: ExternalId/Name/Confidence/NeedsReview/**Candidates**) and `/classify_family`
(SAP group code/name/confidence/needs_review). Per-tenant `Classifier:BaseUrl`. **The DGX box also generates
the Odoo taxonomy bundle and (per the team) hosts the front-end is NOT it — the UI is in this middleware,
see §7.** DGX needs **no** changes for PO creation; for classification quality it just needs the hint
(now provided by enrichment) + its taxonomy to cover product types.

### Taxonomy validator (#195/#197/#198)
`Integrations/Classifier/CategoryTaxonomyService` loads the authoritative Odoo taxonomy from
**`CategoryTaxonomy:FilePath`** (live: `C:\SapOdoo\Config\odoo_taxonomy.json`, **85 categories**). Validates
the `external_id` on the **accept-low-confidence** path only (manual pick + confident DGX are trusted).
**Fail-open** (no bundle → all valid). **Hot-reload** via `FileSystemWatcher` + `POST /api/admin/reload-taxonomy`.
Parses the wrapped form `{ metadata, categories:[…] }`, a bare array, or a `{name:external_id}` map.
`GET /api/admin/taxonomy` feeds the review-UI category picker.

---

## 6. Invoice ingestion (Lubes)

Upload PDF → `InvoiceExtractionJob` (vision/LLM extract, `VisionExtractor`) → `staging_document(_line)` →
`InvoiceAutoMatchJob` (matches `ArticleNumber` → existing OITM `ItemCode` via `ItemExistsAsync`; runs as an
**async job after extraction** — the UI must refresh/poll `review-summary`) → human review →
`InvoiceItemCreationService.BulkCreateAsync` (sequential, per-item timeout, retries `create_failed`) → PO.

Per-line manual-review actions (resolve failures): `match`, `create-new`, `skip`, **`create-with-category`**
(pick a category), **`create-accept-category`** (accept DGX's low-confidence call).

---

## 7. UI

**Server-rendered Razor Pages, served by this middleware** (not a separate repo):
- Lubes: `Pages/Documents/{Index,Upload,Detail}.cshtml` (Detail = the review screen with Bulk Create + the
  per-line ⋮ menu incl. the manual-create actions added in #199).
- Autohub: `Areas/Autohub/Pages/Documents/*`.
- `app.UseStaticFiles()` + `wwwroot` (currently absent → harmless warning).
- The JS uses a small `api()` helper with the `X-Api-Key` header.

🚨 **JSON is `JsonNamingPolicy.SnakeCaseLower` globally** (MVC `AddJsonOptions`). All API request/response
fields are **snake_case** (`unit_price`, `review_status`, `odoo_category_external_id`, `loaded_at`). UI JS
must read/write snake_case (this bit us in #199).

---

## 8. Config, logging, deploy

- **Config:** bundled `appsettings.json` **+ external `C:\SapOdoo\Config\appsettings.Production.json`**
  (loaded last → **overrides**). Sections incl.: `Serilog`, `SapB1`, `Odoo`, `WebhookQueue`, `Classifier`,
  `Pricing`, `BulkCreate`, `OdooBackrefWorker`, `Neon`, `DocumentIngestion`, `VisionExtractor`,
  `AutohubPricing`, `AutohubSkuRefresh`, `Enrichment`, `Companies`, **`LiquiMoly`**, **`Meguin`**,
  **`PurchaseOrders`**, **`CategoryTaxonomy`**.
  - The deployed box sometimes runs an **older bundled `appsettings.json`** → prefer setting things in the
    **external** file (e.g. that's how `IndexCachePath`, `MineMaxMinutes`, taxonomy path got applied).
- **Logs:** Serilog → **`C:\SapOdoo\Logs\app-*.log`** (+ `error-*.log`). **NOT** `…\SAPMiddleware\logs\` and
  **NOT** `middleware*.log` (those are stale). Tail the newest:
  ```powershell
  Get-Content (Get-ChildItem C:\SapOdoo\Logs\app-*.log | Sort LastWriteTime -Desc | Select -First 1).FullName -Tail 60 -Wait
  ```
- **Cache:** `C:\SapOdoo\Cache\*.json` (scraper indexes). Delete to force a rebuild (e.g. after a cache-schema
  change like #200).
- **Start/restart:** `Restart-Service SapOdooMiddleware` (Windows Service). Build/publish on the Windows box.
- **`WebhookQueueProcessor`** polls a SQL Server queue (`ODOO_WEBHOOK_QUEUE`) using `WebhookQueue:ConnectionString`
  (`sa`). It had a `Login failed for user 'sa'` (18456) — a **config/credentials** issue, **non-fatal**,
  unrelated to the features above. Fix the creds or set `WebhookQueue:Enabled=false`.

---

## 9. Conventions / gotchas

- **No CI.** Build correctness = the Windows publish. Be meticulous (DI-API property names, snake_case, the
  stub). When unsure of a SAP DI-API property, mirror an existing working method (`CreateSalesOrderAsync`).
- **Git workflow:** branch `claude/<topic>` off `main` → push → open a **draft PR** → user merges → user
  rebuilds/restarts on Windows. (Watch out: `git reset --hard` discards uncommitted edits — branch first.)
- **DI-API is Windows-only + single-threaded** (the `_lock`).
- **Scraper cache only persists on a healthy build**; schema changes need a cache delete.
- **Classification quality** depends on the `Category` **hint** — enrichment (#200) is the root-cause fix;
  Layer-1 overrides + the accept/pick review actions are backstops.

---

## 10. Recent changes (PRs #180–#200)

- **#180–183, 187** Lubes provisioning: Layer-1 SAP overrides, ≥0.70 family gate, OITB-driven pricing,
  manual "Unit" UoM, brake-fluid override.
- **#182** persist `ItemGroupName` to `NeonProducts`.
- **#185, 189, 192, 198, 200** scraper: persisted/durable index, bounded build, **Meguin brand**, sitemap
  build fix, jsonConfig size, **category enrichment**.
- **#190, 193, 199** category robustness: Odoo Layer-1 (coolant/brake-fluid), `create-with-category` +
  `create-accept-category`, **review-UI manual-create actions**.
- **#195, 197, 198** taxonomy validator (accept-only, hot-reload, dictionary model, wrapped-form parsing).
- **#194** **Lubes Purchase Order** creation; **#196** stub build fix.

## 11. Open items

1. **Barcode importer** — needs the LM/Meguin dealer **EAN/GTIN export** (target the existing
   `PrimaryBarcode`/`AllBarcodes`/`SapUomInfo` columns on `NeonLiquiMolyProducts`).
2. **Autohub Purchase Orders** — parallel `/api/autohub/...` feature (first resolve the SAP-tenant caveat in §1).
3. **DGX-side / front-end PO screen** — wire `purchase-order/preview` + `purchase-order` (the API is done).
4. **Optional PO tweaks** — `DocDate`=invoice date, custom `DocDueDate`.
5. **Verify** the SAP multi-tenancy (does Autohub need a separate SAP company?).

---

## 12. Verification cheatsheet

```powershell
# Restart + tail
Restart-Service SapOdooMiddleware; Start-Sleep 8
Get-Content (Get-ChildItem C:\SapOdoo\Logs\app-*.log | Sort LastWriteTime -Desc | Select -First 1).FullName -Tail 60
# Force scraper re-index (after a cache-schema change)
Stop-Service SapOdooMiddleware; Remove-Item C:\SapOdoo\Cache\*.json -Force; Start-Service SapOdooMiddleware
# Reload taxonomy without restart
# POST /api/admin/reload-taxonomy   (X-Api-Key header)
```
```sql
-- Neon (MolasLUBES): provisioned item + audit
SELECT "ItemCode","ItemGroupCode","ItemGroupName","OdooCategoryName","FamilyOverrideReason"
FROM public."NeonProducts" WHERE "ItemCode" IN ('23139','6452');
-- Scraped source row
SELECT "ArticleNumber","Category","PackagingSize","Liter","ScrapedAt"
FROM public."NeonLiquiMolyProducts" WHERE "ArticleNumber"='6452';
```
