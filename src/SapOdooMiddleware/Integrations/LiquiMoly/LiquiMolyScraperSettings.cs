public class LiquiMolyScraperSettings
{
    public string BaseUrl { get; set; } = "https://www.liqui-moly.com";

    /// <summary>
    /// Storefront/region path for the catalogue SEARCH fallback (e.g. "/en/gb"). The international
    /// "/en" search endpoint returns HTTP 500 and omits region-only products (e.g. Pro-Line items,
    /// which live at "/en/gb/...#&lt;sku&gt;"). Used ONLY for the search fallback — the category crawl
    /// still uses <see cref="CategoryPaths"/>, so already-indexed SKUs are unaffected.
    /// </summary>
    public string SearchStorefrontPath { get; set; } = "/en/gb";

    public int DelayBetweenCategoriesMs { get; set; } = 1200;

    public int MaxParallelRequests { get; set; } = 6;

    public int DelayBetweenRequestsMs { get; set; } = 250;

    /// <summary>
    /// Optional: per-request HTTP timeout in seconds.
    /// Default: 30.
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Browser-like User-Agent sent with every request. Liqui Moly's CDN/bot-protection rejects
    /// requests with no (or a non-browser) User-Agent with HTTP 403, which the scraper sees as
    /// "no data". Override if the site starts blocking this UA.
    /// </summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    /// <summary>
    /// Number of article numbers to scrape per batch.
    /// Smaller batches reduce peak memory usage and allow incremental saves.
    /// Default: 50.
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Maximum number of concurrent HTTP requests during the detail-page enrichment
    /// phase. Higher values speed up enrichment at the cost of being more detectable
    /// as a bot. Default: 1 (fully sequential, safest).
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    /// Mine ALL variant SKUs from each product page during the index build. Category listing pages
    /// only show SOME size-variants as tiles; the rest (e.g. Coolant KFS 18, specific Pro-Line sizes)
    /// are dropped, so those article numbers never make it into the index. The product page exposes
    /// every variant as a <c>variantswitch-sku-{sku}</c> element — mining them makes the index complete.
    /// Costs one extra page fetch per discovered product (the build is cached), so the first build is
    /// slower; warm it via POST /api/liquimoly/scrape/{sku} or raise BulkCreate:PerItemTimeoutSeconds.
    /// </summary>
    public bool MineAllVariants { get; set; } = true;

    /// <summary>
    /// Build the product index in the background on startup so the first /scrape or bulk-create call hits
    /// a warm cache instead of triggering the cold crawl + variant mining (which exceeds the CDN's ~100s
    /// request timeout and returns 524). Set false to revert to lazy build-on-first-request.
    /// </summary>
    public bool WarmupOnStartup { get; set; } = true;

    /// <summary>
    /// How often the background warmup rebuilds the index. Should be slightly below the 23h cache lifetime
    /// so the cache is refreshed before it expires and no request ever pays the cold-build cost.
    /// </summary>
    public int WarmupIntervalHours { get; set; } = 22;

    /// <summary>
    /// File path for the persisted product index. The built index is written here and reloaded on
    /// startup, so a restart skips the multi-minute cold crawl (and the "warming" 503s) as long as the
    /// cached file is within the 23h cache lifetime. Empty → defaults to "Cache/liqui-moly-index-{Brand}.json"
    /// under the app base directory.
    /// </summary>
    public string IndexCachePath { get; set; } = string.Empty;

    /// <summary>
    /// Seed product discovery from LiquiMoly's sitemap — its canonical, complete product list. This finds
    /// products that aren't surfaced in any crawlable category (e.g. Coolant KFS 18 = 23152) and that the
    /// broken (HTTP 500) on-site search can't resolve, so the index is genuinely complete with no manual
    /// per-product URLs. Every sitemap product page is variant-mined alongside the category crawl.
    /// </summary>
    public bool UseSitemap { get; set; } = true;

    /// <summary>
    /// Sitemap(s) to pull product URLs from. Default is the "/en" store sitemap that matches
    /// <see cref="BaseUrl"/>'s /en/ category paths. Product URLs (".../&lt;slug&gt;-pNNNNNN.html") are
    /// extracted and variant-mined; non-product (CMS/news) entries are ignored.
    /// </summary>
    public List<string> SitemapUrls { get; set; } = new()
    {
        "https://www.liqui-moly.com/sitemap/www.liqui-moly.com/sitemap_en.xml",
    };

    /// <summary>
    /// Optional hard-coded OWW API prefix (e.g. "/api/v2/oww/101/TZA/ENG/1").
    /// When empty the prefix is auto-detected from the fragment of the first
    /// oil-guide redirect (e.g. "#oww:/api/v2/oww/101/TZA/ENG/1/...")
    /// Only needed if auto-detection fails or the server is slow to redirect.
    /// </summary>
    public string OwwApiPrefix { get; set; } = string.Empty;

    public Dictionary<string, string> CategoryPaths { get; set; } = new()
    {
        { "/en/engine-oils.html",        "Engine Oils"     },
        { "/en/gear-oils.html",          "Gear Oils"       },
        { "/en/additives.html",          "Additives"       },
        { "/en/vehicle-care.html",       "Vehicle Care"    },
        { "/en/service-products.html",   "Service Products"},
        // Query-param categories whose products aren't surfaced under the slug categories above
        // (e.g. Coolant KFS 18 = 23152, Workshop Pro-Line items). These paginate with "&p=N".
        { "/en/products.html?cat=5016",  "Coolants"        },
        { "/en/products.html?cat=4770",  "Workshop Pro-Line"},
    };
}