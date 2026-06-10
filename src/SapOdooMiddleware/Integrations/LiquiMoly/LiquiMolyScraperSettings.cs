public class LiquiMolyScraperSettings
{
    public string BaseUrl { get; set; } = "https://www.liqui-moly.com";

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
    };
}