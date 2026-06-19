using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using MolasLubes.Infrastructure.Integrations.LiquiMoly;
using Serilog;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Diagnostics;
using SapOdooMiddleware.Filters;
using SapOdooMiddleware.Ingestion;
using SapOdooMiddleware.Integrations.Classifier;
using SapOdooMiddleware.ItemProvisioning;
using SapOdooMiddleware.Middleware;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Pricing;
using SapOdooMiddleware.Services;
using SapOdooMiddleware.Services.Autohub;
using SapOdooMiddleware.Services.Vision;
using SapOdooMiddleware.Workers;

var builder = WebApplication.CreateBuilder(args);

// --- External production config ---
// Load appsettings.Production.json from C:\SapOdoo\Config\ if it exists.
// This survives publish/redeploy since it lives outside the install directory.
var externalConfigDir = Path.Combine("C:", "SapOdoo", "Config");
var externalConfig = Path.Combine(externalConfigDir, "appsettings.Production.json");
if (File.Exists(externalConfig))
{
    builder.Configuration.AddJsonFile(externalConfig, optional: false, reloadOnChange: true);
}

// --- Windows Service hosting ---
// When installed as a Windows Service, UseWindowsService() sets the
// content root correctly and hooks into the SCM lifecycle (start/stop).
// When running interactively (console / dotnet run) this is a no-op.
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "SapOdooMiddleware";
});

// --- Serilog file logging ---
// Reads "Serilog" section from appsettings. Falls back to C:\SapOdoo\Logs if not configured.
builder.Host.UseSerilog((context, configuration) =>
{
    configuration.ReadFrom.Configuration(context.Configuration);
});

// --- Configuration ---
builder.Services.Configure<SapB1Settings>(builder.Configuration.GetSection(SapB1Settings.SectionName));
builder.Services.Configure<OdooSettings>(builder.Configuration.GetSection(OdooSettings.SectionName));
builder.Services.Configure<ApiKeySettings>(builder.Configuration.GetSection(ApiKeySettings.SectionName));
builder.Services.Configure<WebhookQueueSettings>(builder.Configuration.GetSection(WebhookQueueSettings.SectionName));

// --- Services ---
#if WINDOWS_BUILD
builder.Services.AddSingleton<ISapB1Service, SapB1DiApiService>();
// Autohub items are created in a SEPARATE SAP company (Companies:Autohub:SapB1, e.g. "Molas Live 2021").
// Second, independent DI-API connection (its own license seat); resolved only by Autohub provisioning.
builder.Services.AddSingleton<IAutohubSapB1Service>(sp =>
{
    var companies = sp.GetRequiredService<IOptions<CompaniesOptions>>().Value;
    var cfg = companies.Companies.TryGetValue(CompanyContext.AutohubKey, out var c) ? c.SapB1 : null;
    if (cfg is null || string.IsNullOrWhiteSpace(cfg.CompanyDb) || string.IsNullOrWhiteSpace(cfg.Server))
        throw new InvalidOperationException(
            "Companies:Autohub:SapB1 (Server/CompanyDb) is not configured — required to create Autohub items in their own company.");
    return new AutohubSapB1DiApiService(Options.Create(cfg), sp.GetRequiredService<ILogger<SapB1DiApiService>>());
});
#else
builder.Services.AddSingleton<ISapB1Service, SapB1DiApiServiceStub>();
builder.Services.AddSingleton<IAutohubSapB1Service, AutohubSapB1DiApiServiceStub>();
#endif

builder.Services.AddHttpClient<IOdooService, OdooJsonRpcService>();
builder.Services.AddHostedService<WebhookQueueProcessor>();

// --- Item Provisioning settings ---
builder.Services.Configure<ClassifierSettings>(builder.Configuration.GetSection(ClassifierSettings.SectionName));
builder.Services.Configure<PricingSettings>(builder.Configuration.GetSection(PricingSettings.SectionName));
builder.Services.Configure<BulkCreateSettings>(builder.Configuration.GetSection(BulkCreateSettings.SectionName));
builder.Services.Configure<OdooBackrefWorkerSettings>(builder.Configuration.GetSection(OdooBackrefWorkerSettings.SectionName));
builder.Services.Configure<NeonSettings>(builder.Configuration.GetSection(NeonSettings.SectionName));
builder.Services.Configure<LiquiMolyScraperSettings>(builder.Configuration.GetSection("LiquiMoly"));
builder.Services.Configure<MeguinScraperSettings>(builder.Configuration.GetSection("Meguin"));

// --- Multi-tenancy (Companies:* + per-request CompanyContext) ---
builder.Services.Configure<CompaniesOptions>(builder.Configuration);   // binds the "Companies" section
builder.Services.AddScoped<CompanyContext>();
builder.Services.AddScoped<ICompanyContext>(sp => sp.GetRequiredService<CompanyContext>());

// --- DGX classifier typed HttpClient ---
builder.Services.AddHttpClient<ICategoryClassifier, HttpCategoryClassifier>((sp, http) =>
{
    var s = sp.GetRequiredService<IOptions<ClassifierSettings>>().Value;
    http.BaseAddress = new Uri(s.BaseUrl);
    http.Timeout     = TimeSpan.FromSeconds(s.TimeoutSeconds);
});

// --- Liqui Moly scraper (typed HttpClient) ---
// Send browser-like headers: the site's bot protection 403s requests with no/odd User-Agent, which
// the scraper would otherwise surface as "Liqui Moly returned no data for this article".
builder.Services.AddHttpClient<LiquiMolyProductScraperService>((sp, http) =>
{
    var s = sp.GetRequiredService<IOptions<LiquiMolyScraperSettings>>().Value;
    http.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds > 0 ? s.HttpTimeoutSeconds : 30);
    http.DefaultRequestHeaders.UserAgent.ParseAdd(s.UserAgent);
    http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
});

// --- Meguin scraper (LM subsidiary, same platform) — its own typed HttpClient + settings ---
builder.Services.AddHttpClient<MeguinProductScraperService>((sp, http) =>
{
    var s = sp.GetRequiredService<IOptions<MeguinScraperSettings>>().Value;
    http.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds > 0 ? s.HttpTimeoutSeconds : 30);
    http.DefaultRequestHeaders.UserAgent.ParseAdd(s.UserAgent);
    http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
});

// Build each brand's product index in the background (startup + timer) so /scrape and bulk-create hit a
// warm, persisted cache instead of triggering the cold crawl + variant mining (exceeds the ~100s proxy
// timeout). Meguin's catalogue is small, so its one-time crawl is short.
builder.Services.AddHostedService<IndexWarmupHostedService<LiquiMolyProductScraperService, LiquiMolyScraperSettings>>();
builder.Services.AddHostedService<IndexWarmupHostedService<MeguinProductScraperService, MeguinScraperSettings>>();

// --- Item Provisioning components ---
builder.Services.AddSingleton<IPricingCalculator, PricingCalculator>();
builder.Services.AddScoped<INeonLiquiMolyRepository, NeonLiquiMolyRepository>();
builder.Services.Configure<CategoryTaxonomySettings>(builder.Configuration.GetSection(CategoryTaxonomySettings.SectionName));
builder.Services.AddSingleton<ICategoryTaxonomy, CategoryTaxonomyService>();
builder.Services.AddScoped<INeonProductRepository, NeonProductRepository>();
builder.Services.AddScoped<ILubesItemProvisioningService, LubesItemProvisioningService>();

// Async provisioning queue: POST /api/items enqueues; this worker drains it off the
// request thread so the long cold-scrape + classifier work isn't bounded by the
// Cloudflare ~100s proxy timeout. Caller polls GET /api/items/{jobId}.
builder.Services.AddSingleton<IProvisioningJobStore, ProvisioningJobStore>();
builder.Services.AddHostedService<ProvisioningJobWorker>();

// --- Background worker (Odoo id back-stamp to SAP) ---
builder.Services.AddHostedService<OdooBackrefWorker>();

// --- Invoice Ingestion (Phase A) ---
builder.Services.Configure<DocumentIngestionSettings>(
    builder.Configuration.GetSection(DocumentIngestionSettings.SectionName));
builder.Services.Configure<VisionExtractorSettings>(
    builder.Configuration.GetSection(VisionExtractorSettings.SectionName));

builder.Services.AddScoped<IStagingDocumentRepository, StagingDocumentRepository>();
builder.Services.AddScoped<IStagingDocumentLineRepository, StagingDocumentLineRepository>();
// Autohub (parts_catalog) staging repos — tenant-resolved connection string via ICompanyContext.
builder.Services.AddScoped<IStagingPartsDocumentRepository, StagingPartsDocumentRepository>();
builder.Services.AddScoped<IStagingPartsLineRepository, StagingPartsLineRepository>();
builder.Services.AddSingleton<IPdfPageRenderer, PdfPageRenderer>();
builder.Services.AddSingleton<InvoiceTotalsValidator>();
builder.Services.AddScoped<InvoiceExtractionJob>();
builder.Services.AddScoped<DocumentUploadService>();

// Vision extractor: typed HttpClient over the DGX vision endpoint (reuses Classifier:BaseUrl
// with the longer VisionExtractor timeout).
builder.Services.AddHttpClient<IInvoiceExtractor, HttpInvoiceExtractor>((sp, http) =>
{
    var cs = sp.GetRequiredService<IOptions<ClassifierSettings>>().Value;
    var vs = sp.GetRequiredService<IOptions<VisionExtractorSettings>>().Value;
    http.BaseAddress = new Uri(cs.BaseUrl);
    http.Timeout     = TimeSpan.FromSeconds(vs.TimeoutSeconds);
});

// In-process extraction queue + background worker (doc row is the source of truth).
builder.Services.AddSingleton<IDocumentExtractionQueue, DocumentExtractionQueue>();
builder.Services.AddHostedService<InvoiceExtractionWorker>();

// --- Phase B review: auto-match + bulk-create ---
builder.Services.AddScoped<InvoiceAutoMatchJob>();
builder.Services.AddScoped<InvoiceItemCreationService>();
builder.Services.Configure<PurchaseOrderSettings>(builder.Configuration.GetSection(PurchaseOrderSettings.SectionName));
builder.Services.AddScoped<PurchaseOrderService>();
builder.Services.AddSingleton<IDocumentAutoMatchQueue, DocumentAutoMatchQueue>();
builder.Services.AddHostedService<InvoiceAutoMatchWorker>();

// --- Autohub (parts) extraction pipeline — parallel to Lubes, isolated queue/worker ---
builder.Services.AddSingleton<PartsInvoiceValidator>();
builder.Services.AddScoped<PartsExtractionJob>();
builder.Services.AddScoped<PartsDocumentUploadService>();
builder.Services.AddHttpClient<IInvoicePartsExtractor, HttpPartsInvoiceExtractor>((sp, http) =>
{
    // Endpoint + base URL are resolved per-request from the tenant inside the extractor; only the
    // long vision timeout is configured here.
    var vs = sp.GetRequiredService<IOptions<VisionExtractorSettings>>().Value;
    http.Timeout = TimeSpan.FromSeconds(vs.TimeoutSeconds);
});
builder.Services.AddSingleton<IPartsExtractionQueue, PartsExtractionQueue>();
builder.Services.AddHostedService<PartsExtractionWorker>();

// --- Autohub Phase B foundation: pricing/forex/sku tables + pure services (no hosted service) ---
builder.Services.Configure<AutohubPricingSettings>(builder.Configuration.GetSection(AutohubPricingSettings.SectionName));
builder.Services.Configure<AutohubSkuRefreshSettings>(builder.Configuration.GetSection(AutohubSkuRefreshSettings.SectionName));
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IForexRateRepository, ForexRateRepository>();
builder.Services.AddScoped<ISkuCounterRepository, SkuCounterRepository>();
builder.Services.AddScoped<IPricingBrandRatioRepository, PricingBrandRatioRepository>();
builder.Services.AddScoped<IPricingRoundingRuleRepository, PricingRoundingRuleRepository>();
builder.Services.AddSingleton<IOemFilterService, OemFilterService>();          // pure logic, no DB
builder.Services.AddScoped<IForexConversionService, ForexConversionService>();
builder.Services.AddScoped<IPricingCalculationService, PricingCalculationService>();
builder.Services.AddScoped<ISkuGenerationService, SkuGenerationService>();

// SKU counter auto-refresh from SAP (reads the Autohub tenant config directly, so it's a singleton
// usable by both the daily background job and the on-demand /api/admin/sku-counters/refresh endpoint).
builder.Services.AddSingleton<ISapSkuCounterRefreshService, SapSkuCounterRefreshService>();
builder.Services.AddHostedService<SkuCounterRefreshHostedService>();

// --- Autohub Phase B: auto-match + enrichment ---
builder.Services.AddScoped<IOitmMatchRepository, OitmMatchRepository>();
builder.Services.AddScoped<IPartsLineMatchRepository, PartsLineMatchRepository>();
builder.Services.AddScoped<IAutoMatchService, AutoMatchService>();
builder.Services.Configure<EnrichmentSettings>(builder.Configuration.GetSection(EnrichmentSettings.SectionName));
builder.Services.AddScoped<IEnrichmentService, EnrichmentService>();
builder.Services.AddScoped<IEnrichmentResultRouter, EnrichmentResultRouter>();
builder.Services.AddHttpClient<IEnrichmentClient, HttpEnrichmentClient>((sp, http) =>
{
    // Enrichment can trigger a Germax scrape on a cold item — allow the full vision timeout.
    var vs = sp.GetRequiredService<IOptions<VisionExtractorSettings>>().Value;
    http.Timeout = TimeSpan.FromSeconds(vs.TimeoutSeconds);
});
// Background enricher (Q1): auto-enriches pending lines after extraction so review loads ready.
builder.Services.AddHostedService<EnrichmentBackgroundWorker>();
// Startup schema probe: validates the auto-match SQL shapes + status constraints against the live
// DBs and idles the worker on a confirmed mismatch. Registered BEFORE the worker so its StartAsync
// runs first and the guard is set before the first poll.
builder.Services.AddSingleton<SchemaGuard>();
builder.Services.AddHostedService<SchemaProbeService>();
builder.Services.AddHostedService<AutoMatchWorker>();

// --- Autohub Phase B: review + SAP item provisioning ---
builder.Services.AddScoped<IPartsReviewRepository, PartsReviewRepository>();
builder.Services.AddScoped<INeonBridgeService, NeonBridgeService>();
builder.Services.AddScoped<IPartsItemProvisioningService, PartsItemProvisioningService>();
builder.Services.AddScoped<PartsItemCreationService>();

// --- Razor Pages (operator UI under /documents; no Blazor) ---
builder.Services.AddRazorPages();

// --- Controllers ---
builder.Services.AddControllers(options =>
    {
        // Any unhandled exception from an /api/* action returns a JSON error envelope (never a raw
        // exception string), so the browser UI always gets parseable JSON.
        options.Filters.Add<ApiExceptionFilter>();
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    });

// --- Swagger ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SAP-Odoo Middleware API",
        Version = "v1",
        Description = "Click Authorize and enter your API key to authenticate requests in Swagger UI."
    });

    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Name = "X-Api-Key",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Description = "API key via X-Api-Key header (or use Authorization: Bearer token)"
    });

    options.OperationFilter<ApiKeyOperationFilter>();
});

var app = builder.Build();

// --- Swagger UI (Development by default; override with ENABLE_SWAGGER=true) ---
bool enableSwagger = app.Environment.IsDevelopment()
    || string.Equals(Environment.GetEnvironmentVariable("ENABLE_SWAGGER"), "true", StringComparison.OrdinalIgnoreCase);

if (enableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "SAP-Odoo Middleware v1");
    });
}

// --- Startup summary ---
var webhookQueueEnabled = builder.Configuration.GetValue<bool>("WebhookQueue:Enabled");
Log.Information(
    "Middleware started — Environment={Environment}, Swagger={SwaggerEnabled}, WebhookQueue={WebhookQueueEnabled}, ExternalConfig={ExternalConfigLoaded}",
    app.Environment.EnvironmentName,
    enableSwagger,
    webhookQueueEnabled,
    File.Exists(externalConfig));

// --- Static files (served before the API-key check; not under /api) ---
app.UseStaticFiles();

// --- Middleware ---
app.UseMiddleware<TenantResolutionMiddleware>();   // sets tenant from URL prefix (default Lubes)
app.UseMiddleware<ApiKeyMiddleware>();

// --- Endpoints ---
app.MapControllers();
app.MapRazorPages();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();

// Make Program class accessible for integration tests
public partial class Program { }