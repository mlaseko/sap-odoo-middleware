using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MolasLubes.Infrastructure.Integrations.LiquiMoly;

public class LiquiMolyProductScraperService
{
    private readonly HttpClient _http;
    private readonly LiquiMolyScraperSettings _settings;
    // Non-generic so subclasses can pass ILogger<SubclassType> without casting.
    private readonly ILogger _logger;
    private readonly string _logPrefix;

    // Per-brand index cache: key = BrandKey
    private static readonly ConcurrentDictionary<string, (
        Dictionary<string, string> Index,          // SKU  → full URL#fragment
        Dictionary<string, string> SkuSizes,       // SKU  → size label ("1 l", "20 l")
        Dictionary<string, List<string>> AllSizes, // base URL (no #) → all size labels
        DateTimeOffset BuiltAt)>
        _brandCache = new(StringComparer.OrdinalIgnoreCase);

    // Per-brand SKU → category-name (the DGX hint), captured during the category crawl. Parallel to
    // _brandCache and persisted in the same index file; a missing/old cache simply yields no hint.
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>>
        _brandCategories = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(23);

    // Serialises index builds across all callers so the expensive cold build runs once, not N times.
    private static readonly SemaphoreSlim _buildLock = new(1, 1);

    // On-disk shape of the index, so a restart can skip the cold crawl.
    private sealed record PersistedIndex(
        DateTimeOffset BuiltAt,
        Dictionary<string, string> Index,
        Dictionary<string, string> SkuSizes,
        Dictionary<string, List<string>> AllSizes,
        Dictionary<string, string>? Categories = null);   // SKU → category name (optional; absent in old caches)

    private static readonly JsonSerializerOptions _indexJson =
        new() { PropertyNameCaseInsensitive = true };

    private string IndexCacheFilePath =>
        string.IsNullOrWhiteSpace(_settings.IndexCachePath)
            ? Path.Combine(AppContext.BaseDirectory, "Cache", $"liqui-moly-index-{BrandKey}.json")
            : _settings.IndexCachePath;

    /// <summary>
    /// Loads the persisted index from disk and populates the in-memory cache, but only if the file exists
    /// and is within <see cref="CacheLifetime"/>. Returns the index tuple on success, null otherwise.
    /// </summary>
    private (Dictionary<string, string>, Dictionary<string, string>, Dictionary<string, List<string>>)?
        TryLoadIndexFromDisk()
    {
        try
        {
            var file = IndexCacheFilePath;
            if (!File.Exists(file)) return null;

            var p = JsonSerializer.Deserialize<PersistedIndex>(File.ReadAllText(file), _indexJson);
            if (p?.Index is null || p.Index.Count == 0) return null;

            var age = DateTimeOffset.UtcNow - p.BuiltAt;
            if (age >= CacheLifetime)
            {
                _logger.LogInformation(_logPrefix + "Persisted index is stale ({Age:F1}h old); will rebuild.", age.TotalHours);
                return null;
            }

            var index    = new Dictionary<string, string>(p.Index, StringComparer.OrdinalIgnoreCase);
            var skuSizes = new Dictionary<string, string>(p.SkuSizes ?? new(), StringComparer.OrdinalIgnoreCase);
            var allSizes = new Dictionary<string, List<string>>(p.AllSizes ?? new(), StringComparer.OrdinalIgnoreCase);
            _brandCache[BrandKey] = (index, skuSizes, allSizes, p.BuiltAt);
            _brandCategories[BrandKey] = new Dictionary<string, string>(p.Categories ?? new(), StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation(_logPrefix + "Loaded product index from disk | {Count} SKUs | {Cats} categorised | built {Built:u} ({Age:F1}h ago)",
                index.Count, _brandCategories[BrandKey].Count, p.BuiltAt, age.TotalHours);
            return (index, skuSizes, allSizes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, _logPrefix + "Failed to load persisted index from disk; will rebuild.");
            return null;
        }
    }

    /// <summary>Persists a freshly-built index to disk (atomic replace) for reuse across restarts.</summary>
    private void SaveIndexToDisk(
        Dictionary<string, string> index,
        Dictionary<string, string> skuSizes,
        Dictionary<string, List<string>> allSizes,
        Dictionary<string, string> categories,
        DateTimeOffset builtAt)
    {
        try
        {
            var file = IndexCacheFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var json = JsonSerializer.Serialize(new PersistedIndex(builtAt, index, skuSizes, allSizes, categories), _indexJson);
            var tmp  = file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, file, overwrite: true);
            _logger.LogInformation(_logPrefix + "Persisted product index to {File} | {Count} SKUs", file, index.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, _logPrefix + "Failed to persist product index to disk.");
        }
    }

    // Exposed for subclasses to give each brand its own cache slot and log prefix.
    protected virtual string BrandKey   => "LiquiMoly";
    // Includes a trailing space so log messages read naturally when concatenated.
    protected virtual string LogPrefix  => "[LiquiMoly] ";

    // Valid numeric SKU: 3–6 digits
    private static readonly Regex ValidSkuPattern =
        new(@"^\d{3,6}$", RegexOptions.Compiled);

    private static readonly Regex SizePattern =
        new(@"\b(\d+(?:[.,]\d+)?\s*(?:ml|l|kg|g))\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SpecGradePattern =
        new(@"\b\d{1,2}W[-–]\d{2,3}\b|\bSAE\s+\d+\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Safety limit on paginated category pages to prevent runaway fetching
    private const int MaxCategoryPages = 50;

    // Public DI constructor for direct registration.
    public LiquiMolyProductScraperService(
        HttpClient httpClient,
        IOptions<LiquiMolyScraperSettings> settings,
        ILogger<LiquiMolyProductScraperService> logger)
        : this(httpClient, settings.Value, logger) { }

    // Protected constructor used by subclasses that supply their own settings value and logger.
    protected LiquiMolyProductScraperService(
        HttpClient httpClient,
        LiquiMolyScraperSettings settings,
        ILogger logger)
    {
        _http      = httpClient;
        _settings  = settings;
        _logger    = logger;
        _logPrefix = LogPrefix;
    }

    // ======================================================
    // MAIN ENTRY
    // ======================================================

    public async Task<List<LiquiMolyProductDto>> ScrapeByArticleNumbersAsync(
        IEnumerable<string> articleNumbers,
        CancellationToken ct = default)
    {
        var raw = articleNumbers?.ToList() ?? new List<string>();

        _logger.LogInformation(_logPrefix + "Raw SKU input count: {Count}", raw.Count);

        if (raw.Count > 0)
            _logger.LogInformation(_logPrefix + "First 10 raw SKUs: {Skus}",
                string.Join(", ", raw.Take(10)));

        var targets = new HashSet<string>(
            raw.Where(x => !string.IsNullOrWhiteSpace(x))
               .Select(x => x.Trim())
               .Where(x => ValidSkuPattern.IsMatch(x)),
            StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(_logPrefix + "Valid numeric SKUs after filtering: {Count}", targets.Count);

        if (targets.Count > 0)
            _logger.LogInformation(_logPrefix + "First 10 cleaned SKUs: {Skus}",
                string.Join(", ", targets.Take(10)));

        var (index, skuSizes, allSizes) = await GetOrBuildIndexAsync(ct);
        _brandCategories.TryGetValue(BrandKey, out var skuCategories);

        _logger.LogInformation(_logPrefix + "Product index size: {Count}", index.Count);

        var results = new ConcurrentBag<LiquiMolyProductDto>();
        var missedSkus = new ConcurrentBag<string>();
        int found = 0, missing = 0;

        await ForEachBoundedAsync(targets, _settings.MaxParallelRequests, async sku =>
        {
            if (!index.TryGetValue(sku, out var url))
            {
                missedSkus.Add(sku);
                return;
            }

            Interlocked.Increment(ref found);
            _logger.LogDebug(_logPrefix + "SKU {Sku} resolved → {Url}", sku, url);

            skuSizes.TryGetValue(sku, out var skuSize);
            var baseUrl = url.Contains('#') ? url[..url.IndexOf('#')] : url;
            allSizes.TryGetValue(baseUrl, out var productSizes);
            string? skuCat = null; skuCategories?.TryGetValue(sku, out skuCat);

            var dto = await ScrapeProductPageForSkuAsync(sku, url, skuSize, productSizes, skuCat, ct);
            if (dto != null)
                results.Add(dto);

        }, ct);

        // Fallback: search Magento catalogue for each SKU that was not in the index
        if (!missedSkus.IsEmpty)
        {
            _logger.LogInformation(_logPrefix + "Attempting search fallback for {Count} missing SKU(s)", missedSkus.Count);

            await ForEachBoundedAsync(missedSkus, _settings.MaxParallelRequests, async sku =>
            {
                var url = await TrySearchForSkuAsync(sku, ct);
                if (url == null)
                {
                    Interlocked.Increment(ref missing);
                    _logger.LogWarning(_logPrefix + "SKU {Sku} not found in index or via search", sku);
                    return;
                }

                Interlocked.Increment(ref found);
                _logger.LogDebug(_logPrefix + "SKU {Sku} resolved via search → {Url}", sku, url);

                string? skuCatF = null; skuCategories?.TryGetValue(sku, out skuCatF);
                var dto = await ScrapeProductPageForSkuAsync(sku, url, null, null, skuCatF, ct);
                if (dto != null)
                    results.Add(dto);
            }, ct);
        }

        _logger.LogInformation(
            _logPrefix + "Scrape summary | Requested={Requested} | FoundInIndex={Found} | Missing={Missing} | Scraped={Scraped}",
            targets.Count, found, missing, results.Count);

        return results.ToList();
    }

    // ======================================================
    // INDEX — GET CACHED OR REBUILD
    // ======================================================

    private async Task<(Dictionary<string, string> Index,
                         Dictionary<string, string> SkuSizes,
                         Dictionary<string, List<string>> AllSizes)>
        GetOrBuildIndexAsync(CancellationToken ct)
    {
        if (_brandCache.TryGetValue(BrandKey, out var cached)
            && cached.Index.Count > 0
            && DateTimeOffset.UtcNow - cached.BuiltAt < CacheLifetime)
        {
            _logger.LogInformation(_logPrefix + "Using cached index | {Count} SKUs", cached.Index.Count);
            return (cached.Index, cached.SkuSizes, cached.AllSizes);
        }

        // Stampede guard: the cold build (category crawl + variant mining) takes well over a minute,
        // so serialise concurrent callers — the first builds, the rest reuse the freshly cached result
        // instead of each kicking off their own crawl.
        await _buildLock.WaitAsync(ct);
        try
        {
            if (_brandCache.TryGetValue(BrandKey, out var fresh)
                && fresh.Index.Count > 0
                && DateTimeOffset.UtcNow - fresh.BuiltAt < CacheLifetime)
            {
                _logger.LogInformation(_logPrefix + "Using cached index (built while waiting) | {Count} SKUs", fresh.Index.Count);
                return (fresh.Index, fresh.SkuSizes, fresh.AllSizes);
            }

            // Reuse a still-fresh index persisted by a previous run before paying the cold crawl.
            if (TryLoadIndexFromDisk() is { } fromDisk)
                return fromDisk;

            _logger.LogInformation(_logPrefix + "Building product index from category pages...");
            return await BuildProductIndexAsync(ct);
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <summary>
    /// Builds (or rebuilds) the product index off the request path. The background warmup service calls
    /// this on startup and on a timer so HTTP (/scrape) and bulk-create callers always hit a warm cache
    /// instead of paying the cold crawl + variant-mining cost (which exceeds the CDN's request timeout).
    /// </summary>
    public Task WarmIndexAsync(CancellationToken ct) => WarmIndexAsync(forceRebuild: false, ct);

    /// <summary>
    /// Ensures the index is warm. With <paramref name="forceRebuild"/> false (startup), an in-memory or
    /// on-disk index that is still fresh is reused — so a restart loads instantly instead of paying the
    /// cold crawl. With it true (periodic refresh), the index is always rebuilt from the live site and
    /// re-persisted.
    /// </summary>
    public async Task WarmIndexAsync(bool forceRebuild, CancellationToken ct)
    {
        await _buildLock.WaitAsync(ct);
        try
        {
            if (!forceRebuild)
            {
                if (_brandCache.TryGetValue(BrandKey, out var c)
                    && c.Index.Count > 0
                    && DateTimeOffset.UtcNow - c.BuiltAt < CacheLifetime)
                {
                    _logger.LogInformation(_logPrefix + "Index already warm in memory | {Count} SKUs", c.Index.Count);
                    return;
                }
                if (TryLoadIndexFromDisk() is not null)
                    return;
            }

            await BuildProductIndexAsync(ct);
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <summary>
    /// True when a freshly-built index is cached, so a scrape resolves from memory instead of triggering
    /// the long cold build. The /scrape HTTP endpoint uses this to fail fast (503) while the background
    /// warmup is still running, rather than blocking past the CDN's ~100s request timeout (524).
    /// </summary>
    public bool IsIndexWarm() =>
        _brandCache.TryGetValue(BrandKey, out var c)
        && c.Index.Count > 0
        && DateTimeOffset.UtcNow - c.BuiltAt < CacheLifetime;

    /// <summary>
    /// Builds the SKU → product URL index.
    ///
    /// The Liqui-Moly site now embeds SKUs directly in the URL fragment of every
    /// variant link on category listing pages:
    ///   <c>&lt;a class="product-variation ..." href="...product-url.html#1024"&gt;</c>
    ///
    /// This means the entire index can be built in a single category-page crawl —
    /// no separate product-page visits are needed for variant discovery.
    ///
    /// Process:
    ///   1. Fetch each category from <see cref="LiquiMolyScraperSettings.CategoryPaths"/>
    ///      (all pagination pages).
    ///   2. Parse every <c>a.product-variation</c> link; extract SKU from the URL
    ///      fragment (e.g. <c>#1024</c>) and map it directly to the href.
    ///   3. For any product URL whose fragment is absent or non-numeric (rare
    ///      single-variant products), fetch the product page and extract the SKU
    ///      from <c>&lt;span itemprop="sku"&gt;</c>.
    /// </summary>
    private async Task<(Dictionary<string, string> Index,
                         Dictionary<string, string> SkuSizes,
                         Dictionary<string, List<string>> AllSizes)>
        BuildProductIndexAsync(CancellationToken ct)
    {
        var map             = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var needsProductFetch = new ConcurrentBag<string>();
        var skuSizes        = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allSizesByBase  = new ConcurrentDictionary<string, ConcurrentBag<string>>(StringComparer.OrdinalIgnoreCase);
        var skuCategories   = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Phase 1 — crawl category pages; extract SKU→URL, SKU→size and SKU→category from the listing.
        foreach (var (path, categoryName) in _settings.CategoryPaths)
        {
            if (ct.IsCancellationRequested) break;

            await CollectSkuUrlsFromCategoryAsync(
                _settings.BaseUrl.TrimEnd('/') + path, categoryName,
                map, needsProductFetch, skuSizes, allSizesByBase, skuCategories, ct);

            await Task.Delay(_settings.DelayBetweenCategoriesMs, ct);
        }

        _logger.LogInformation(
            _logPrefix + "Category crawl complete | Direct SKU mappings={Direct} | Need product page fetch={Fetch}",
            map.Count, needsProductFetch.Count);

        // Abort only when there's nothing left to try. With UseSitemap, an empty category crawl is normal
        // (e.g. Meguin runs sitemap-only with no CategoryPaths) — Phase 2b below discovers products from the
        // sitemap, so don't bail here.
        if (map.Count == 0 && needsProductFetch.IsEmpty && !_settings.UseSitemap)
        {
            _logger.LogError(
                _logPrefix + "No product URLs found — category pages may be blocked or have changed structure");
            var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return (empty, empty, new());
        }

        // Phase 2 (rare) — for products whose URL had no numeric SKU fragment,
        // fetch the product page and read <span itemprop="sku">
        if (!needsProductFetch.IsEmpty)
        {
            await ForEachBoundedAsync(needsProductFetch, _settings.MaxParallelRequests, async productUrl =>
            {
                try
                {
                    var html = await FetchHtmlAsync(productUrl, ct);
                    if (string.IsNullOrWhiteSpace(html)) return;

                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);
                    var pageSku = ExtractSkuFromPage(doc);
                    if (!string.IsNullOrWhiteSpace(pageSku))
                        map.TryAdd(pageSku, productUrl + "#" + pageSku);

                    await Task.Delay(_settings.DelayBetweenRequestsMs, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, _logPrefix + "Failed extracting SKU from {Url}", productUrl);
                }
            }, ct);
        }

        // Phase 2b — mine EVERY variant SKU from each product page. Two URL sources, unioned so each page
        // is fetched once:
        //   (a) products found via the category crawl — recovers size-variants the listing tiles drop.
        //   (b) the sitemap (LiquiMoly:SitemapUrls) — recovers "orphan" products LiquiMoly doesn't list in
        //       ANY crawlable category (e.g. Coolant KFS 18 = 23152) and that its broken (HTTP 500) on-site
        //       search can't resolve either. This is what makes the index genuinely complete.
        // The product page lists every variant as `variantswitch-sku-{sku}`.
        if (_settings.MineAllVariants || _settings.UseSitemap)
        {
            var productBaseUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // (a) category-discovered product pages
            if (_settings.MineAllVariants)
            {
                foreach (var key in allSizesByBase.Keys) productBaseUrls.Add(key);
                foreach (var url in map.Values)
                    productBaseUrls.Add(url.Contains('#') ? url[..url.IndexOf('#')] : url);
            }

            // (b) every product URL published in the sitemap
            var fromCategories = productBaseUrls.Count;
            if (_settings.UseSitemap)
            {
                foreach (var url in await FetchSitemapProductUrlsAsync(ct))
                    productBaseUrls.Add(url);
            }

            var before = map.Count;

            // Bound this phase: under CDN throttling it can balloon to hours. When the budget is hit we
            // stop gracefully and finalise the index with whatever was mined (the category crawl already
            // gave a near-complete index). Linked to the app token so shutdown still cancels promptly.
            using var mineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_settings.MineMaxMinutes > 0)
                mineCts.CancelAfter(TimeSpan.FromMinutes(_settings.MineMaxMinutes));
            var mineCt = mineCts.Token;

            var total = productBaseUrls.Count;
            int done  = 0;
            try
            {
                await ForEachBoundedAsync(productBaseUrls, _settings.MaxParallelRequests, async baseUrl =>
                {
                    try
                    {
                        var html = await FetchHtmlAsync(baseUrl, mineCt);
                        if (!string.IsNullOrWhiteSpace(html))
                        {
                            var doc = new HtmlDocument();
                            doc.LoadHtml(html);
                            foreach (var sku in ExtractVariantSkusFromPage(doc))
                                map.TryAdd(sku, baseUrl + "#" + sku);
                        }

                        var n = Interlocked.Increment(ref done);
                        if (n % 250 == 0)
                            _logger.LogInformation(_logPrefix + "Variant mining progress: {Done}/{Total} pages | {Skus} SKUs",
                                n, total, map.Count);

                        await Task.Delay(_settings.DelayBetweenRequestsMs, mineCt);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, _logPrefix + "Variant mining failed for {Url}", baseUrl);
                    }
                }, mineCt);
            }
            catch (OperationCanceledException) when (mineCt.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    _logPrefix + "Variant-mining budget ({Min} min) reached at {Done}/{Total} pages; " +
                    "finalising index with {Skus} SKUs.", _settings.MineMaxMinutes, done, total, map.Count);
            }

            _logger.LogInformation(
                _logPrefix + "Variant mining: +{New} SKU(s) from {Products} product page(s) " +
                "({Cat} via categories, {Sitemap} added via sitemap) (index {Before} -> {After})",
                map.Count - before, productBaseUrls.Count, fromCategories,
                productBaseUrls.Count - fromCategories, before, map.Count);
        }

        var result   = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        var sizesMap = new Dictionary<string, string>(skuSizes, StringComparer.OrdinalIgnoreCase);
        var categoriesMap = new Dictionary<string, string>(skuCategories, StringComparer.OrdinalIgnoreCase);
        var allSizes = allSizesByBase.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(_logPrefix + "Index complete | SKUs={Count} | WithSize={Sized} | WithCategory={Cat}",
            result.Count, sizesMap.Count, categoriesMap.Count);

        // Only promote a healthy build to the shared/disk cache. A build below the floor (e.g. the category
        // crawl itself got throttled/blocked) is returned to the immediate caller but NOT cached or
        // persisted, so a broken/partial index can't poison the 23h cache; the warmup retries soon instead.
        if (result.Count >= Math.Max(1, _settings.MinIndexSkuCount))
        {
            _logger.LogInformation(_logPrefix + "Sample SKUs: {Skus}",
                string.Join(", ", result.Keys.Take(10)));

            var builtAt = DateTimeOffset.UtcNow;
            _brandCache[BrandKey] = (result, sizesMap, allSizes, builtAt);
            _brandCategories[BrandKey] = categoriesMap;
            SaveIndexToDisk(result, sizesMap, allSizes, categoriesMap, builtAt);
        }
        else
        {
            _logger.LogError(
                _logPrefix + "Product index too small ({Count} < {Min}) — likely throttled/blocked; " +
                "NOT caching or persisting. Will rebuild on the next warmup tick.",
                result.Count, Math.Max(1, _settings.MinIndexSkuCount));
        }

        return (result, sizesMap, allSizes);
    }

    // ======================================================
    // CATEGORY PAGE COLLECTION
    // ======================================================

    /// <summary>
    /// Crawls all paginated pages of a category and populates <paramref name="map"/>
    /// with SKU → URL entries extracted directly from
    /// <c>&lt;a class="product-variation" href="...url.html#SKU"&gt;</c> links.
    ///
    /// If a product link has no numeric SKU fragment (rare single-variant products),
    /// the base URL is added to <paramref name="needsProductFetch"/> for a follow-up
    /// product-page fetch.
    /// </summary>
    private async Task CollectSkuUrlsFromCategoryAsync(
        string categoryUrl,
        string categoryName,
        ConcurrentDictionary<string, string> map,
        ConcurrentBag<string> needsProductFetch,
        ConcurrentDictionary<string, string> skuSizes,
        ConcurrentDictionary<string, ConcurrentBag<string>> allSizesByBase,
        ConcurrentDictionary<string, string> skuCategories,
        CancellationToken ct)
    {
        var firstHtml = await FetchHtmlAsync(categoryUrl, ct);
        if (string.IsNullOrWhiteSpace(firstHtml))
        {
            _logger.LogWarning(_logPrefix + "Category '{Category}' returned empty response", categoryName);
            return;
        }

        var firstDoc = new HtmlDocument();
        firstDoc.LoadHtml(firstHtml);
        ExtractSkuMappingsFromPage(firstDoc, map, needsProductFetch, skuSizes, allSizesByBase, categoryName, skuCategories);

        int totalPages = Math.Min(ExtractTotalPages(firstDoc), MaxCategoryPages);

        _logger.LogInformation(
            _logPrefix + "Category '{Category}' has {Pages} page(s)",
            categoryName, totalPages);

        if (totalPages > 1)
        {
            await ForEachBoundedAsync(
                Enumerable.Range(2, totalPages - 1),
                _settings.MaxParallelRequests,
                async page =>
                {
                    await Task.Delay(_settings.DelayBetweenRequestsMs, ct);

                    // Append the page param correctly whether the category URL is a clean slug
                    // ("/en/engine-oils.html") or already carries a query string
                    // ("/en/products.html?cat=5016", which paginates as "...&p=2").
                    var pageUrl = categoryUrl.Contains('?')
                        ? $"{categoryUrl}&p={page}"
                        : $"{categoryUrl}?p={page}";
                    var html = await FetchHtmlAsync(pageUrl, ct);
                    if (string.IsNullOrWhiteSpace(html)) return;

                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);
                    ExtractSkuMappingsFromPage(doc, map, needsProductFetch, skuSizes, allSizesByBase, categoryName, skuCategories);
                }, ct);
        }

        _logger.LogInformation(
            _logPrefix + "Category '{Category}' → index now has {Count} SKU mappings",
            categoryName, map.Count);
    }

    /// <summary>
    /// Parses all <c>a.product-variation</c> links on a listing page.
    /// Each href is expected to look like <c>https://…/product-name-pNNNNNN.html#SKU</c>
    /// where the fragment is the numeric article number.
    /// </summary>
    private static void ExtractSkuMappingsFromPage(
        HtmlDocument doc,
        ConcurrentDictionary<string, string> map,
        ConcurrentBag<string> needsProductFetch,
        ConcurrentDictionary<string, string>? skuSizes = null,
        ConcurrentDictionary<string, ConcurrentBag<string>>? allSizesByBase = null,
        string? categoryName = null,
        ConcurrentDictionary<string, string>? skuCategories = null)
    {
        var links = doc.DocumentNode.SelectNodes("//a[contains(@class,'product-variation')]");
        if (links == null) return;

        var seenBaseUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in links)
        {
            var href = link.GetAttributeValue("href", null)?.Trim();
            if (string.IsNullOrWhiteSpace(href) || !href.StartsWith("http")) continue;

            var hashIdx = href.IndexOf('#');
            if (hashIdx > 0 && hashIdx < href.Length - 1)
            {
                var fragment = href[(hashIdx + 1)..];
                if (ValidSkuPattern.IsMatch(fragment))
                {
                    // Fragment IS the SKU — map it directly
                    map.TryAdd(fragment, href);
                    if (!string.IsNullOrWhiteSpace(categoryName))
                        skuCategories?.TryAdd(fragment, categoryName!);

                    // Extract size from tag-badge > span.value
                    if (skuSizes != null || allSizesByBase != null)
                    {
                        var sizeNode = link.SelectSingleNode(
                            ".//div[contains(@class,'tag-badge')]//span[contains(@class,'value')]")
                            ?? link.SelectSingleNode(".//span[contains(@class,'value')]");

                        var sizeText = sizeNode == null
                            ? null
                            : HtmlEntity.DeEntitize(sizeNode.InnerText.Trim());

                        if (!string.IsNullOrWhiteSpace(sizeText))
                        {
                            skuSizes?.TryAdd(fragment, sizeText);

                            if (allSizesByBase != null)
                            {
                                var baseUrl2 = href[..hashIdx];
                                var bag = allSizesByBase.GetOrAdd(baseUrl2, _ => new ConcurrentBag<string>());
                                bag.Add(sizeText);
                            }
                        }
                    }
                    continue;
                }
            }

            // No numeric SKU fragment — queue the base product page for a separate fetch
            var baseUrl = hashIdx > 0 ? href[..hashIdx] : href;
            if (baseUrl.Contains(".html") && seenBaseUrls.Add(baseUrl))
                needsProductFetch.Add(baseUrl);
        }
    }

    /// <summary>
    /// Extracts every variant article number from a product page. Each size-variant is rendered in an
    /// element whose class contains <c>variantswitch-sku-{sku}</c> (the same marker the image/size/download
    /// extractors key off), so we pull the numeric SKU out of every such class. This recovers variants
    /// that the category listing never showed as their own tile.
    /// </summary>
    private static readonly Regex VariantSwitchSkuPattern =
        new(@"variantswitch-sku-(\d{3,6})\b", RegexOptions.Compiled);

    private static IEnumerable<string> ExtractVariantSkusFromPage(HtmlDocument doc)
    {
        var nodes = doc.DocumentNode.SelectNodes("//*[contains(@class,'variantswitch-sku-')]");
        if (nodes == null) yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var cls = node.GetAttributeValue("class", "");
            foreach (Match m in VariantSwitchSkuPattern.Matches(cls))
            {
                var sku = m.Groups[1].Value;
                if (seen.Add(sku)) yield return sku;
            }
        }
    }

    // Product page URLs in the sitemap look like ".../en/<slug>-pNNNNNN.html". News/CMS pages don't
    // match this, so it cleanly isolates the ~966 product pages from the ~1800 total sitemap entries.
    private static readonly Regex SitemapProductUrlPattern =
        new(@"https?://[^\s<>""]+?-p\d+\.html", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches a product-page href (absolute or relative), e.g. "/en/gb/molygen-motor-protect-p001522.html".
    // Used by the search fallback to find result-tile product pages to variant-mine.
    private static readonly Regex ProductHrefPattern =
        new(@"-p\d+\.html", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Pulls every product-page URL out of the configured sitemap(s). The sitemap is LiquiMoly's canonical,
    /// complete product list, so it surfaces items missing from the category tree. Failures are logged and
    /// skipped — the category crawl still provides a working index if the sitemap is unreachable.
    /// </summary>
    private async Task<List<string>> FetchSitemapProductUrlsAsync(CancellationToken ct)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sitemapUrl in _settings.SitemapUrls)
        {
            if (string.IsNullOrWhiteSpace(sitemapUrl)) continue;
            try
            {
                var xml = await FetchHtmlAsync(sitemapUrl, ct);
                if (string.IsNullOrWhiteSpace(xml))
                {
                    _logger.LogWarning(_logPrefix + "Sitemap returned empty: {Url}", sitemapUrl);
                    continue;
                }

                var added = 0;
                foreach (Match m in SitemapProductUrlPattern.Matches(xml))
                    if (urls.Add(HtmlEntity.DeEntitize(m.Value))) added++;

                _logger.LogInformation(_logPrefix + "Sitemap {Url}: {Count} product URL(s)", sitemapUrl, added);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, _logPrefix + "Sitemap fetch failed: {Url}", sitemapUrl);
            }
        }

        return urls.ToList();
    }

    private static int ExtractTotalPages(HtmlDocument doc)
    {
        // Pagination links look like "...?p=N" (slug categories, e.g. /en/engine-oils.html?p=2)
        // or "...?cat=5016&p=N" (query-param categories, where the page param is "&p=", encoded
        // in the markup as "&amp;p="). Match the number after either '?p=' or '&p='.
        var pageLinks = doc.DocumentNode.SelectNodes("//a[contains(@href,'p=')]");
        if (pageLinks == null) return 1;

        int max = 1;
        foreach (var link in pageLinks)
        {
            var href = HtmlEntity.DeEntitize(link.GetAttributeValue("href", "")) ?? "";
            var m = Regex.Match(href, @"[?&]p=(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var p) && p > max)
                max = p;
        }

        return max;
    }

    // ======================================================
    // SCRAPE PRODUCT PAGE
    // ======================================================

    private async Task<LiquiMolyProductDto?> ScrapeProductPageForSkuAsync(
        string requestedSku,
        string productUrlWithHash,
        string? cachedSize,
        List<string>? cachedAllSizes,
        string? cachedCategory,
        CancellationToken ct)
    {
        // The hash (#sku) is handled client-side by Magento's JS — strip it before fetching
        var pageUrl = productUrlWithHash.Contains('#')
            ? productUrlWithHash[..productUrlWithHash.IndexOf('#')]
            : productUrlWithHash;

        // Fire both requests concurrently — product page HTML and PIM sheets are independent.
        var htmlTask = FetchHtmlAsync(pageUrl, ct);
        var pimTask  = FetchPimSheetsAsync(requestedSku, ct);
        await Task.WhenAll(htmlTask, pimTask);

        var html = htmlTask.Result;
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var name        = ExtractName(doc);
        var (desc, application) = ExtractDescriptionAndApplication(doc);
        var images      = ExtractAllImages(doc, requestedSku);
        var (cat, sub)  = ExtractCategories(doc);
        var (specificationItems, approvals, recommendations) = ExtractApprovalSpecificationData(doc);
        var overviewProperties = ExtractOverviewProperties(doc);

        // Sizes: prefer the value captured from the category listing during index build. For
        // sitemap-discovered orphans there's no cached size, so fall back to the product page's embedded
        // Magento configurable config (jsonConfig.gebindeinhalt — authoritative per-variant size), then to
        // name-based HTML extraction as a last resort.
        string? currentSize = cachedSize;
        List<string> allPackagingSizes = cachedAllSizes ?? new List<string>();
        if (currentSize == null || allPackagingSizes.Count == 0)
        {
            var (cfgSize, cfgAll) = ExtractSizesFromJsonConfig(html, requestedSku);
            currentSize ??= cfgSize;
            if (allPackagingSizes.Count == 0 && cfgAll.Count > 0)
                allPackagingSizes = cfgAll;
        }
        if (currentSize == null)
        {
            // Last resort: name-based extraction
            var (htmlSize, htmlAll) = ExtractPackagingSizes(doc, requestedSku, name);
            currentSize ??= htmlSize;
            if (allPackagingSizes.Count == 0) allPackagingSizes = htmlAll;
        }

        // Try PIM API first for download URLs; fall back to HTML scraping.
        string? pdf = null, sds = null;
        var pimSheets = pimTask.Result;
        if (pimSheets != null)
        {
            pdf = SelectPimProductInfoUrl(pimSheets);
            sds = SelectPimSdsUrl(pimSheets);
            pimSheets.Dispose();
        }
        if (pdf == null || sds == null)
        {
            var (htmlPdf, htmlSds) = ExtractDownloads(doc, requestedSku);
            pdf ??= htmlPdf;
            sds ??= htmlSds;
        }

        // SpecGrade: name first, then description
        var specGrade = ExtractSpecGrade(name ?? "")
                     ?? ExtractSpecGrade(desc ?? "");

        return new LiquiMolyProductDto
        {
            ArticleNumber         = requestedSku,
            Name                  = name ?? requestedSku,
            Description           = desc,
            ProductUrl            = productUrlWithHash,
            ImageUrl              = images.FirstOrDefault(),
            AllImageUrls          = images,
            PackagingSize         = currentSize,
            AllPackagingSizes     = allPackagingSizes,
            Liter                 = ParseLiters(currentSize),
            // Prefer the category captured from the crawl (the listing page we found this SKU on) — the
            // product page's own breadcrumb is JS-rendered and usually absent from the static HTML.
            Category              = !string.IsNullOrWhiteSpace(cachedCategory) ? cachedCategory : cat,
            SubCategory           = sub,
            Specifications        = new Dictionary<string, string>(),
            SpecificationItems    = specificationItems,
            Approvals             = approvals,
            OverviewProperties    = overviewProperties,
            Application           = application,
            LiquiMolyRecommendations = recommendations,
            SpecGrade             = specGrade,
            ProductInfoPdfUrl     = pdf,
            SafetyDataSheetPdfUrl = sds,
        };
    }

    // ======================================================
    // EXTRACTION HELPERS  (all Magento 2 / Liqui-Moly specific)
    // ======================================================

    /// <summary>Product name from &lt;h1 class="page-title"&gt; or any &lt;h1&gt;.</summary>
    private static string? ExtractName(HtmlDocument doc)
    {
        var node = doc.DocumentNode.SelectSingleNode("//h1[contains(@class,'page-title')]")
                ?? doc.DocumentNode.SelectSingleNode("//h1");

        return node == null ? null : HtmlEntity.DeEntitize(node.InnerText.Trim());
    }

    /// <summary>
    /// Product description and application text from the Description tab section.
    /// The page often renders the "Application" heading inside the same container as
    /// the main description, followed by usage instructions and then the SKU table.
    /// </summary>
    private static (string? Description, string? Application) ExtractDescriptionAndApplication(HtmlDocument doc)
    {
        var node = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'product-info-description')]")
                ?? doc.DocumentNode.SelectSingleNode("//div[@itemprop='description']")
                ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'description')]");

        if (node == null)
            return (null, null);

        var lines = HtmlEntity.DeEntitize(node.InnerText)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !IsNoiseLine(x))
            .ToList();

        if (lines.Count == 0)
            return (null, null);

        var applicationIndex = lines.FindIndex(x =>
            x.Equals("Application", StringComparison.OrdinalIgnoreCase));

        if (applicationIndex < 0)
            return (string.Join(Environment.NewLine + Environment.NewLine, lines), null);

        var descriptionLines = lines
            .Take(applicationIndex)
            .ToList();

        var applicationLines = lines
            .Skip(applicationIndex + 1)
            .TakeWhile(x => !IsDescriptionSectionStopLine(x))
            .ToList();

        var description = descriptionLines.Count == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, descriptionLines);

        var application = applicationLines.Count == 0
            ? null
            : string.Join(" ", applicationLines);

        return (description, application);
    }

    /// <summary>
    /// All product images from the gallery panel.
    ///
    /// The outer gallery container is <c>#gallery-preview-{sku}</c>; inside it the
    /// preview images live in <c>div.product-gallery-preview-media</c> panels and
    /// thumbnails in <c>div.product-gallery-preview-thumbnails</c>.
    /// Product images always have <c>/media/catalog/product/</c> in their URL;
    /// logos, footers and icons do not — so that substring is used as a filter.
    /// </summary>
    private static List<string> ExtractAllImages(HtmlDocument doc, string requestedSku)
    {
        var ranked = new List<(string Url, int Score)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Tier 1: variant-specific gallery. This is the most reliable source for the
        // exact package size image, e.g. a 205 l barrel instead of a 5 l bottle.
        AddRankedImageUrls(
            ranked,
            seen,
            doc.DocumentNode.SelectNodes(
                $"//div[@id='gallery-preview-{requestedSku}']//div[contains(@class,'product-gallery-preview-media')]//img"),
            requestedSku,
            baseScore: 300);

        // Tier 2: variant-specific block fallback. Some pages hide the gallery inside
        // variantswitch-sku-{sku} containers without exposing the expected preview id first.
        AddRankedImageUrls(
            ranked,
            seen,
            doc.DocumentNode.SelectNodes(
                $"//div[contains(@class,'variantswitch-sku-{requestedSku}')]//img"),
            requestedSku,
            baseScore: 220);

        // Tier 3: page-wide gallery fallback if the exact variant markup is missing.
        AddRankedImageUrls(
            ranked,
            seen,
            doc.DocumentNode.SelectNodes(
                "//div[contains(@class,'product-gallery-preview-media')]//img"),
            requestedSku,
            baseScore: 120);

        // Tier 4: broader gallery-image-* fallback.
        AddRankedImageUrls(
            ranked,
            seen,
            doc.DocumentNode.SelectNodes(
                "//div[starts-with(@id,'gallery-image-')]//img"),
            requestedSku,
            baseScore: 80);

        // Final fallback: product image anchors.
        AddRankedAnchorUrls(
            ranked,
            seen,
            doc.DocumentNode.SelectNodes("//a[contains(@href,'pim.liqui-moly.de/ws/media/article-image/')]"),
            requestedSku,
            baseScore: 40);

        return ranked
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Url)
            .ToList();
    }

    private static void AddRankedImageUrls(
        List<(string Url, int Score)> ranked,
        HashSet<string> seen,
        HtmlNodeCollection? nodes,
        string requestedSku,
        int baseScore)
    {
        if (nodes == null) return;

        foreach (var node in nodes)
        {
            var url = GetImageUrl(node);
            if (!TryNormalizeProductImageUrl(url, out var normalized))
                continue;

            if (!seen.Add(normalized))
                continue;

            ranked.Add((normalized, ScoreProductImageUrl(normalized, requestedSku, baseScore)));
        }
    }

    private static void AddRankedAnchorUrls(
        List<(string Url, int Score)> ranked,
        HashSet<string> seen,
        HtmlNodeCollection? anchors,
        string requestedSku,
        int baseScore)
    {
        if (anchors == null) return;

        foreach (var anchor in anchors)
        {
            var href = anchor.GetAttributeValue("href", null);
            if (!TryNormalizeProductImageUrl(href, out var normalized))
                continue;

            if (!seen.Add(normalized))
                continue;

            ranked.Add((normalized, ScoreProductImageUrl(normalized, requestedSku, baseScore)));
        }
    }

    private static string? GetImageUrl(HtmlNode node)
    {
        // Liqui Moly product pages often use ci-src instead of src/data-src.
        return BestUrlFromSrcset(node.GetAttributeValue("data-srcset", null))
            ?? node.GetAttributeValue("ci-src", null)
            ?? node.GetAttributeValue("src", null)
            ?? node.GetAttributeValue("data-src", null);
    }

    private static bool TryNormalizeProductImageUrl(string? rawUrl, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(rawUrl))
            return false;

        var candidate = HtmlEntity.DeEntitize(rawUrl).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        if (!candidate.Contains("/media/catalog/product/", StringComparison.OrdinalIgnoreCase)
         && !candidate.Contains("pim.liqui-moly.de/ws/media/article-image/", StringComparison.OrdinalIgnoreCase))
            return false;

        normalized = candidate;
        return true;
    }

    private static int ScoreProductImageUrl(string url, string requestedSku, int baseScore)
    {
        var score = baseScore;

        if (url.Contains($"/{requestedSku}_", StringComparison.OrdinalIgnoreCase)
         || url.Contains($"{requestedSku}_", StringComparison.OrdinalIgnoreCase)
         || url.Contains($"/{requestedSku}.", StringComparison.OrdinalIgnoreCase)
         || url.Contains($"{requestedSku}.", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        // Product pack shots are usually named with the SKU and product title.
        // Lifestyle/marketing tiles often contain LM_A_quad and are less useful as the lead image.
        if (url.Contains("_LM_A_quad_", StringComparison.OrdinalIgnoreCase))
            score -= 35;
        else
            score += 20;

        // Prefer larger preview/modal images over small thumbnails.
        if (url.Contains("w=800", StringComparison.OrdinalIgnoreCase))
            score += 20;
        else if (url.Contains("w=365", StringComparison.OrdinalIgnoreCase))
            score += 10;
        else if (url.Contains("w=100", StringComparison.OrdinalIgnoreCase))
            score -= 20;

        return score;
    }

    /// Parses an HTML srcset attribute and returns the URL with the highest
    /// pixel-density descriptor (e.g. "2x"), giving the largest available image.
    /// Returns null if <paramref name="srcset"/> is null or unparseable.
    ///
    /// Example input: "https://…?w=365 1x, https://…?w=548 1.5x, https://…?w=730 2x"
    /// Returns the "https://…?w=730" URL.
    private static string? BestUrlFromSrcset(string? srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset)) return null;

        string? bestUrl = null;
        double bestDensity = -1;

        foreach (var entry in srcset.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1) continue;

            var url = parts[0].Trim();
            double density = 1.0;

            if (parts.Length == 2)
            {
                var descriptor = parts[1].Trim().TrimEnd('x');
                double.TryParse(descriptor, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out density);
            }

            if (density > bestDensity)
            {
                bestDensity = density;
                bestUrl = url;
            }
        }

        return bestUrl;
    }

    /// <summary>
    /// Category and sub-category from the Magento 2 breadcrumb navigation.
    ///
    /// Page breadcrumb: Home | Products | Oils | Top Tec 4200 5W-30 New Generation
    /// → Category = "Oils", SubCategory = null (only one intermediate crumb)
    ///
    /// If deeper: Home | Products | Oils | Motor Oils | Product Name
    /// → Category = "Oils", SubCategory = "Motor Oils"
    /// </summary>
    private static (string? category, string? subCategory) ExtractCategories(HtmlDocument doc)
    {
        // Magento 2 confirmed structure: <ol class="breadcrumb"><li><a>...</a></li></ol>
        var ol = doc.DocumentNode.SelectSingleNode("//ol[contains(@class,'breadcrumb')]")
              ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'breadcrumbs')]//ul");

        if (ol == null) return (null, null);

        var crumbs = ol.SelectNodes("li/a")
            ?.Select(n => HtmlEntity.DeEntitize(n.InnerText.Trim()))
            .Where(t => !string.IsNullOrWhiteSpace(t)
                     && !t.Equals("Home", StringComparison.OrdinalIgnoreCase)
                     && !t.Equals("Products", StringComparison.OrdinalIgnoreCase))
            .ToList()
            ?? new List<string>();

        // The last anchor in the breadcrumb is the current product page — drop it
        // so that only genuine category crumbs remain.
        if (crumbs.Count > 0)
            crumbs.RemoveAt(crumbs.Count - 1);

        return (
            crumbs.Count >= 1 ? crumbs[0] : null,
            crumbs.Count >= 2 ? crumbs[1] : null
        );
    }

    /// <summary>
    /// Extracts specification items, OEM approvals, and LIQUI MOLY recommendations
    /// from the "Approvals &amp; Specifications" tab.
    /// </summary>
    private static (List<string> SpecificationItems, List<string> Approvals, List<string> Recommendations)
        ExtractApprovalSpecificationData(HtmlDocument doc)
    {
        var approvalDivSelectors = new[]
        {
            "//div[@id='tab-detail-approvalsandspecifications']",
            "//div[contains(@class,'approvals')]",
            "//div[contains(@class,'approval')]",
            "//section[contains(@class,'approval')]",
        };

        foreach (var sel in approvalDivSelectors)
        {
            var node = doc.DocumentNode.SelectSingleNode(sel);
            if (node == null) continue;

            var extracted = ParseApprovalSpecificationSection(HtmlEntity.DeEntitize(node.InnerText));
            if (extracted.SpecificationItems.Count > 0 ||
                extracted.Approvals.Count > 0 ||
                extracted.Recommendations.Count > 0)
            {
                return extracted;
            }
        }

        var boldHeadings = doc.DocumentNode
            .SelectNodes("//strong | //b")
            ?.Where(n => n.InnerText.Contains("Specifications", StringComparison.OrdinalIgnoreCase)
                      && n.InnerText.Contains("Approvals", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (boldHeadings != null)
        {
            foreach (var heading in boldHeadings)
            {
                // Collect text from the parent element that contains the heading
                var parent = heading.ParentNode;
                if (parent == null) continue;

                var fullText = HtmlEntity.DeEntitize(parent.InnerText);
                var extracted = ParseApprovalSpecificationSection(fullText);
                if (extracted.SpecificationItems.Count > 0 ||
                    extracted.Approvals.Count > 0 ||
                    extracted.Recommendations.Count > 0)
                {
                    return extracted;
                }
            }
        }

        return (new List<string>(), new List<string>(), new List<string>());
    }

    private static (List<string> SpecificationItems, List<string> Approvals, List<string> Recommendations)
        ParseApprovalSpecificationSection(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (new List<string>(), new List<string>(), new List<string>());

        var normalized = NormalizeWhitespace(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return (new List<string>(), new List<string>(), new List<string>());

        const string recommendationLead =
            "LIQUI MOLY recommends this product for vehicles or assemblies for which the following specifications or original spare part numbers are required";

        var specsText = string.Empty;
        var recommendationsText = string.Empty;

        var leadIndex = normalized.IndexOf(recommendationLead, StringComparison.OrdinalIgnoreCase);
        if (leadIndex >= 0)
        {
            var prefix = normalized[..leadIndex].Trim();
            specsText = StripSpecificationsHeading(prefix);

            var suffix = normalized[(leadIndex + recommendationLead.Length)..].TrimStart(' ', ':');
            recommendationsText = CutAtFirstSectionHeading(suffix);
        }
        else
        {
            specsText = CutAtFirstSectionHeading(StripSpecificationsHeading(normalized));
        }

        var specAndApprovalItems = ParseCommaSeparatedItems(specsText);
        var recommendations = ParseCommaSeparatedItems(recommendationsText);

        var specificationItems = specAndApprovalItems
            .Where(IsSpecificationItem)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var approvals = specAndApprovalItems
            .Where(x => !IsSpecificationItem(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (specificationItems, approvals, recommendations);
    }

    private static List<string> ParseCommaSeparatedItems(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();

        return text
            .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 2 && p.Length < 180)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Extracts the bullet-point overview properties shown in the product overview block:
    /// div.product.attribute.properties ul.check-list li.value span
    /// This includes items inside the hidden "show more" container.
    /// </summary>
    private static List<string> ExtractOverviewProperties(HtmlDocument doc)
    {
        var nodes = doc.DocumentNode.SelectNodes(
            "//div[contains(@class,'product') and contains(@class,'attribute') and contains(@class,'properties')]" +
            "//ul[contains(@class,'check-list')]//li[contains(@class,'value')]//span");

        if (nodes == null || nodes.Count == 0)
            return new List<string>();

        return nodes
            .Select(node => HtmlEntity.DeEntitize(node.InnerText.Trim()))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsNoiseLine(string text) =>
        text.Equals("Description", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("Learn More", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("Show more", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("Show details", StringComparison.OrdinalIgnoreCase);

    private static bool IsDescriptionSectionStopLine(string text) =>
        text.Equals("SKU", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("Informations", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("Container type", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("Container contents", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("Language line", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("PU", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("Pallet unit", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("Sea pallet unit", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("Specifications / Approvals", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("Product Information", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("Downloads", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWhitespace(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();

    private static string StripSpecificationsHeading(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return Regex.Replace(
            text,
            @"^\s*Specifications\s*/\s*Approvals\s*:?\s*",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();
    }

    private static string CutAtFirstSectionHeading(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var headings = new[]
        {
            "Product Information",
            "Safety data sheets",
            "Images and documents",
            "Downloads",
            "Show all variants"
        };

        var cutIndex = text.Length;
        foreach (var heading in headings)
        {
            var idx = text.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < cutIndex)
                cutIndex = idx;
        }

        return text[..cutIndex].Trim();
    }

    private static bool IsSpecificationItem(string item)
    {
        if (string.IsNullOrWhiteSpace(item))
            return false;

        var normalized = item.Trim();
        return normalized.StartsWith("ACEA ", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("API ", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("ILSAC ", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("JASO ", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("SAE ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the packaging size for the requested SKU variant (currentSize) and
    /// all available sizes across every variant of this product (allSizes).
    ///
    /// Strategy:
    /// 1. Swatch options — works for both &lt;div&gt; and &lt;a&gt; elements.
    ///    The element with class "selected" or "active" is the current variant.
    /// 2. Description table — the row whose first cell matches the requested SKU;
    ///    the "Container contents" cell provides currentSize.
    /// 3. <c>variantswitch-sku-{sku}</c> div inner text.
    /// 4. Fallback: product name.
    /// </summary>
    /// <summary>
    /// Reads packaging sizes from the product page's embedded Magento configurable config. LM exposes a
    /// <c>"jsonConfig": { … }</c> object containing two parallel maps keyed by Magento entity id:
    /// <c>"sku"</c> (entity → article number) and <c>"gebindeinhalt"</c> (entity → size label, e.g. "1 l").
    /// Returns the size for <paramref name="sku"/> plus every distinct size on the product. This is the
    /// reliable source for sitemap-discovered orphans whose size isn't captured from a category listing.
    /// </summary>
    private static (string? size, List<string> allSizes) ExtractSizesFromJsonConfig(string? html, string sku)
    {
        var all = new List<string>();
        if (string.IsNullOrEmpty(html)) return (null, all);
        try
        {
            var json = ExtractBalancedJsonObject(html, "\"jsonConfig\"");
            if (json == null) return (null, all);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("gebindeinhalt", out var sizeMap)
                || sizeMap.ValueKind != JsonValueKind.Object)
                return (null, all);

            foreach (var entry in sizeMap.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String) continue;
                var s = entry.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s) && !all.Contains(s!)) all.Add(s!);
            }

            string? size = null;
            if (root.TryGetProperty("sku", out var skuMap) && skuMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in skuMap.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.String
                        && string.Equals(entry.Value.GetString(), sku, StringComparison.OrdinalIgnoreCase)
                        && sizeMap.TryGetProperty(entry.Name, out var sv)
                        && sv.ValueKind == JsonValueKind.String)
                    {
                        size = sv.GetString();
                        break;
                    }
                }
            }
            return (size, all);
        }
        catch
        {
            return (null, all);
        }
    }

    /// <summary>
    /// Extracts the first balanced <c>{ … }</c> JSON object that appears after <paramref name="afterToken"/>
    /// in <paramref name="text"/>, honouring string literals/escapes. Used to pull an embedded JSON blob
    /// (e.g. Magento's jsonConfig) out of a larger script body for parsing.
    /// </summary>
    private static string? ExtractBalancedJsonObject(string text, string afterToken)
    {
        var tokenIdx = text.IndexOf(afterToken, StringComparison.Ordinal);
        if (tokenIdx < 0) return null;
        var start = text.IndexOf('{', tokenIdx + afterToken.Length);
        if (start < 0) return null;

        int depth = 0;
        bool inString = false, escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text.Substring(start, i - start + 1);
        }
        return null;
    }

    private static (string? currentSize, List<string> allSizes) ExtractPackagingSizes(
        HtmlDocument doc,
        string requestedSku,
        string? name)
    {
        string? currentSize = null;
        var allSizes = new List<string>();

        // ── 1. Swatch options ────────────────────────────────────────────────
        // The swatches live inside div.product-add-form
        // (CSS: div.product-info-main > div.product-add-form).
        // Liqui-Moly renders them as <a class="swatch-option text" option-label="5 l">
        // or <div class="swatch-option text">.
        var formRoot = doc.DocumentNode.SelectSingleNode(
                           "//div[contains(@class,'product-add-form')]")
                    ?? doc.DocumentNode;

        var swatchOpts = formRoot.SelectNodes(
                ".//*[contains(@class,'swatch-option') and contains(@class,'text')]")
             ?? formRoot.SelectNodes(
                ".//*[contains(@class,'swatch-option')]");

        if (swatchOpts != null)
        {
            foreach (var opt in swatchOpts)
            {
                var label = opt.GetAttributeValue("option-label", null)
                         ?? opt.GetAttributeValue("data-option-label", null)
                         ?? HtmlEntity.DeEntitize(opt.InnerText.Trim());

                if (string.IsNullOrWhiteSpace(label)) continue;

                foreach (Match m in SizePattern.Matches(label))
                {
                    var size = m.Groups[1].Value;
                    if (!allSizes.Contains(size, StringComparer.OrdinalIgnoreCase))
                        allSizes.Add(size);

                    // The selected/active swatch is this SKU's size
                    if (currentSize == null)
                    {
                        var cls = opt.GetAttributeValue("class", "");
                        if (cls.Contains("selected", StringComparison.OrdinalIgnoreCase)
                         || cls.Contains("active",   StringComparison.OrdinalIgnoreCase))
                            currentSize = size;
                    }
                }
            }
        }

        // ── 2. Description table — row whose first cell = requestedSku ───────
        // The page has a table: SKU | Container type | Container contents | …
        if (currentSize == null)
        {
            var skuCell = doc.DocumentNode.SelectSingleNode(
                $"//td[normalize-space(.)='{requestedSku}'" +
                $" or .//a[contains(@href,'#{requestedSku}')]]");

            var row = skuCell?.ParentNode; // <tr>
            if (row != null)
            {
                foreach (var cell in row.SelectNodes("td") ?? Enumerable.Empty<HtmlNode>())
                {
                    var m = SizePattern.Match(HtmlEntity.DeEntitize(cell.InnerText.Trim()));
                    if (!m.Success) continue;

                    currentSize = m.Groups[1].Value;
                    if (!allSizes.Contains(currentSize, StringComparer.OrdinalIgnoreCase))
                        allSizes.Add(currentSize);
                    break;
                }
            }
        }

        // ── 3. variantswitch-sku-{sku} div ───────────────────────────────────
        if (currentSize == null)
        {
            var variantDiv = doc.DocumentNode
                .SelectSingleNode($"//div[contains(@class,'variantswitch-sku-{requestedSku}')]");

            if (variantDiv != null)
            {
                var m = SizePattern.Match(HtmlEntity.DeEntitize(variantDiv.InnerText));
                if (m.Success)
                {
                    currentSize = m.Groups[1].Value;
                    if (!allSizes.Contains(currentSize, StringComparer.OrdinalIgnoreCase))
                        allSizes.Add(currentSize);
                }
            }
        }

        // ── 4. Fallback: product name ─────────────────────────────────────────
        if (currentSize == null && !string.IsNullOrWhiteSpace(name))
        {
            var m = SizePattern.Match(name);
            if (m.Success)
            {
                currentSize = m.Groups[1].Value;
                if (!allSizes.Contains(currentSize, StringComparer.OrdinalIgnoreCase))
                    allSizes.Add(currentSize);
            }
        }

        return (currentSize, allSizes);
    }

    /// <summary>
    /// Extracts the English product information PDF URL and the English safety data sheet URL
    /// from the Downloads tab.
    ///
    /// The downloads section is scoped to the variant-specific container:
    ///   <c>div.variantswitch-sku-{sku}.downloads</c>
    ///
    /// Product info sheets:  <c>pim.liqui-moly.de/ws/pi/article/{id}?language=en</c>
    /// Safety data sheets:   <c>sichdatonline.chemical-check.de/…_EN.pdf</c>
    ///   or (fallback)       <c>static.liqui-moly.com/…_EN.pdf</c>
    ///
    /// English is identified by <c>language=en</c> in the PI URL, or by the presence of
    /// a <c>flag-icon--gb</c> icon + "English" label span in the SDS row.
    /// </summary>
    private static (string? pdfUrl, string? sdsUrl) ExtractDownloads(HtmlDocument doc, string requestedSku)
    {
        string? pdfUrl = null;
        string? sdsUrl = null;

        // ── Scope to the variant-specific downloads section ───────────────────
        // <div class="variantswitch-sku-{sku} downloads container ...">
        var downloadsDiv = doc.DocumentNode.SelectSingleNode(
            $"//div[contains(@class,'variantswitch-sku-{requestedSku}') and contains(@class,'downloads')]");
        var root = downloadsDiv ?? doc.DocumentNode;

        // ── Product Information (pim.liqui-moly.de/ws/pi/) ───────────────────
        var piLinks = root.SelectNodes(".//a[contains(@href,'pim.liqui-moly.de/ws/pi/')]");
        if (piLinks != null)
        {
            // Prefer British English (language=en), then US English (language=us)
            var pick = piLinks.FirstOrDefault(n =>
                            n.GetAttributeValue("href", "")
                             .Contains("language=en", StringComparison.OrdinalIgnoreCase))
                    ?? piLinks.FirstOrDefault(n =>
                            n.GetAttributeValue("href", "")
                             .Contains("language=us", StringComparison.OrdinalIgnoreCase))
                    ?? piLinks[0];

            var h = pick.GetAttributeValue("href", null);
            if (!string.IsNullOrWhiteSpace(h)) pdfUrl = h;
        }

        // ── Safety Data Sheet ─────────────────────────────────────────────────
        // Links may be on chemical-check.de or static.liqui-moly.com (.pdf).
        // English is identified by flag-icon--gb + "English" span, or _EN.pdf in URL.
        var sdsLinks = root.SelectNodes(
            ".//a[contains(@href,'chemical-check.de') or " +
            "(contains(@href,'static.liqui-moly.com') and contains(@href,'.pdf'))]");

        if (sdsLinks != null)
        {
            // Best match: GB-flag icon + "English" label span
            var pick = sdsLinks.FirstOrDefault(n =>
                {
                    var hasGbFlag = n.SelectSingleNode(".//i[contains(@class,'flag-icon--gb')]") != null;
                    var span      = n.SelectSingleNode(".//span[contains(@class,'fw-bold')]");
                    var isEnglish = span?.InnerText.Trim()
                                       .Equals("English", StringComparison.OrdinalIgnoreCase) == true;
                    return hasGbFlag && isEnglish;
                })
                // Fallback 1: _EN.pdf suffix in URL
                ?? sdsLinks.FirstOrDefault(n =>
                    n.GetAttributeValue("href", "").Contains("_EN.pdf", StringComparison.OrdinalIgnoreCase))
                // Fallback 2: any link whose label span says "English"
                ?? sdsLinks.FirstOrDefault(n =>
                {
                    var span = n.SelectSingleNode(".//span[contains(@class,'fw-bold')]");
                    return span?.InnerText.Trim()
                               .Equals("English", StringComparison.OrdinalIgnoreCase) == true;
                })
                ?? sdsLinks[0];

            var h = pick.GetAttributeValue("href", null);
            if (!string.IsNullOrWhiteSpace(h)) sdsUrl = h;
        }

        // ── Fallback: generic .pdf scan (only if scoped search above found nothing) ──
        if (pdfUrl == null || sdsUrl == null)
        {
            var pdfLinks = doc.DocumentNode.SelectNodes("//a[contains(@href,'.pdf')]");
            if (pdfLinks != null)
            {
                foreach (var link in pdfLinks)
                {
                    var href = link.GetAttributeValue("href", null);
                    if (string.IsNullOrWhiteSpace(href)) continue;

                    var hrefL  = href.ToLowerInvariant();
                    var textL  = link.InnerText.ToLowerInvariant();
                    bool isSds = textL.Contains("safety") || textL.Contains("sds")
                              || hrefL.Contains("safety") || hrefL.Contains("sds")
                              || hrefL.Contains("chemical-check");

                    if (isSds  && sdsUrl == null) sdsUrl = href;
                    else if (!isSds && pdfUrl == null) pdfUrl = href;

                    if (pdfUrl != null && sdsUrl != null) break;
                }
            }
        }

        return (pdfUrl, sdsUrl);
    }

    /// <summary>
    /// Parses a volume-in-litres decimal from a size label such as "1 l", "20 l", "500 ml".
    /// Returns null for weight-only labels ("5 kg") or when the input is unparseable.
    /// </summary>
    private static decimal? ParseLiters(string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return null;
        var m = Regex.Match(size.Trim(), @"(\d+(?:[.,]\d+)?)\s*(ml|l)\b", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var num = decimal.Parse(
            m.Groups[1].Value.Replace(',', '.'),
            NumberStyles.Number,
            CultureInfo.InvariantCulture);
        return m.Groups[2].Value.Equals("ml", StringComparison.OrdinalIgnoreCase)
            ? num / 1000m
            : num;
    }

    private static string? ExtractSpecGrade(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = SpecGradePattern.Match(text);
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// Tries to extract the displayed SKU from a single-variant Magento 2 product page.
    /// Looks for <c>&lt;span itemprop="sku"&gt;</c>.
    /// </summary>
    private static string? ExtractSkuFromPage(HtmlDocument doc)
    {
        var node = doc.DocumentNode.SelectSingleNode("//span[@itemprop='sku']")
                ?? doc.DocumentNode.SelectSingleNode(
                       "//div[contains(@class,'product-info-stock-sku')]//span[@class='value']");

        if (node == null) return null;
        var text = HtmlEntity.DeEntitize(node.InnerText.Trim());
        return ValidSkuPattern.IsMatch(text) ? text : null;
    }

    // ======================================================
    // PIM API  —  structured download URLs
    // ======================================================

    /// <summary>
    /// Fetches the PIM sheets JSON for a given variant/SKU.
    /// Endpoint: https://pim.liqui-moly.com/sheets/{sku}
    /// Returns a parsed <see cref="JsonDocument"/> or null when unavailable.
    /// Caller must dispose the returned document.
    /// </summary>
    private async Task<JsonDocument?> FetchPimSheetsAsync(string sku, CancellationToken ct)
    {
        var url = $"https://pim.liqui-moly.com/sheets/{sku}";
        try
        {
            var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug(_logPrefix + "PIM sheets not available for SKU {Sku} (HTTP {Status})",
                    sku, (int)resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, _logPrefix + "PIM sheets fetch failed for SKU {Sku}", sku);
            return null;
        }
    }

    /// <summary>
    /// Selects the English product-information PDF URL from a PIM sheets document.
    /// Preference: en_GB → any en_* key → first available URL.
    /// </summary>
    private static string? SelectPimProductInfoUrl(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("productinformation", out var pi)
            || pi.ValueKind != JsonValueKind.Object)
            return null;

        // en_GB preferred
        if (pi.TryGetProperty("en_GB", out var enGb)
            && enGb.TryGetProperty("url", out var u1) && u1.ValueKind == JsonValueKind.String)
            return u1.GetString();

        // Any en_* key
        foreach (var prop in pi.EnumerateObject())
        {
            if (!prop.Name.StartsWith("en_", StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.TryGetProperty("url", out var u2) && u2.ValueKind == JsonValueKind.String)
                return u2.GetString();
        }

        // First available
        foreach (var prop in pi.EnumerateObject())
        {
            if (prop.Value.TryGetProperty("url", out var u3) && u3.ValueKind == JsonValueKind.String)
                return u3.GetString();
        }

        return null;
    }

    /// <summary>
    /// Selects the English safety-data-sheet PDF URL from a PIM sheets document.
    /// Keeps only locales whose key starts with "en_" AND whose URL contains "_EN" (case-insensitive).
    /// </summary>
    private static string? SelectPimSdsUrl(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("safetydatasheets", out var sds)
            || sds.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var prop in sds.EnumerateObject())
        {
            if (!prop.Name.StartsWith("en_", StringComparison.OrdinalIgnoreCase)) continue;
            if (!prop.Value.TryGetProperty("url", out var urlEl)
                || urlEl.ValueKind != JsonValueKind.String) continue;

            var url = urlEl.GetString();
            if (url != null && url.Contains("_EN", StringComparison.OrdinalIgnoreCase))
                return url;
        }

        return null;
    }

    // ======================================================
    // SEARCH FALLBACK
    // ======================================================

    /// <summary>
    /// Searches the Magento 2 catalogue for a single SKU and returns its product URL
    /// (with fragment) if found, or null.
    ///
    /// Endpoint: {SearchStorefrontPath}/catalogsearch/result/?q={sku} (e.g. /en/gb/...).
    /// The result page contains the same <c>a.product-variation</c> links as category
    /// pages, so we can reuse <see cref="ExtractSkuMappingsFromPage"/>.
    /// </summary>
    private async Task<string?> TrySearchForSkuAsync(string sku, CancellationToken ct)
    {
        // Search on the regional storefront (e.g. "/en/gb"): the international "/en" search 500s and
        // omits region-only products (e.g. Pro-Line), which is why those SKUs miss the "/en" index.
        var region = "/" + (_settings.SearchStorefrontPath ?? "/en/gb").Trim('/');
        var searchUrl = _settings.BaseUrl.TrimEnd('/')
                      + region + "/catalogsearch/result/?q=" + Uri.EscapeDataString(sku);
        try
        {
            var html = await FetchHtmlAsync(searchUrl, ct);
            if (string.IsNullOrWhiteSpace(html)) return null;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Reuse the same extractor — collect into a temp map
            var map = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var discard = new ConcurrentBag<string>();
            ExtractSkuMappingsFromPage(doc, map, discard);

            if (map.TryGetValue(sku, out var url))
                return url;

            // Fallback: product-page SKU span in case the search returned a direct page
            var pageSku = ExtractSkuFromPage(doc);
            if (pageSku == sku)
            {
                var canonical = doc.DocumentNode
                    .SelectSingleNode("//link[@rel='canonical']")
                    ?.GetAttributeValue("href", null);
                if (!string.IsNullOrWhiteSpace(canonical))
                    return canonical + "#" + sku;
            }

            // Search results list products as tiles (".../<slug>-pNNNNNN.html"), NOT a.product-variation,
            // and a region-only variant (e.g. a GB-store SKU like #1015 that the "/en" index never sees)
            // is only exposed on its product page. Probe the result product pages and variant-mine each
            // (same `variantswitch-sku-{sku}` marker the index build uses) until one carries this SKU.
            var candidates = (doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
                .Select(a => a.GetAttributeValue("href", ""))
                .Where(h => !string.IsNullOrWhiteSpace(h) && ProductHrefPattern.IsMatch(h))
                .Select(h => h.Contains('#') ? h[..h.IndexOf('#')] : h)
                .Select(h => h.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? h
                    : _settings.BaseUrl.TrimEnd('/') + "/" + h.TrimStart('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var pageHtml = await FetchHtmlAsync(candidate, ct);
                if (string.IsNullOrWhiteSpace(pageHtml)) continue;

                var pdoc = new HtmlDocument();
                pdoc.LoadHtml(pageHtml);
                if (ExtractVariantSkusFromPage(pdoc).Contains(sku))
                {
                    _logger.LogInformation(
                        _logPrefix + "SKU {Sku} resolved via search → product-page probe → {Url}", sku, candidate);
                    return candidate + "#" + sku;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, _logPrefix + "Search fallback failed for SKU {Sku}", sku);
            return null;
        }
    }

    // ======================================================
    // HTTP  —  with simple exponential-backoff retry
    // ======================================================

    private async Task<string> FetchHtmlAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        const int maxRetries = 2;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsStringAsync(ct);

                _logger.LogWarning(
                    _logPrefix + "HTTP {Status} for {Url} (attempt {A}/{Max})",
                    (int)resp.StatusCode, url, attempt + 1, maxRetries + 1);

                // Server errors (5xx) are not transient — don't retry.
                if ((int)resp.StatusCode >= 500)
                    return string.Empty;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < maxRetries)
            {
                _logger.LogWarning(ex,
                    _logPrefix + "Fetch error for {Url} (attempt {A}/{Max})",
                    url, attempt + 1, maxRetries + 1);

                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                continue;
            }
            catch
            {
                break;
            }
        }

        return string.Empty;
    }

    // ======================================================
    // HELPERS
    // ======================================================

    private string BuildAbsolute(string path)
    {
        if (path.StartsWith("http")) return path;
        return _settings.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    private string? BuildAbsoluteOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return BuildAbsolute(path);
    }

    private static async Task ForEachBoundedAsync<T>(
        IEnumerable<T> items,
        int maxParallel,
        Func<T, Task> action,
        CancellationToken ct)
    {
        using var sem = new SemaphoreSlim(maxParallel);

        var tasks = items.Select(async item =>
        {
            await sem.WaitAsync(ct);
            try { await action(item); }
            finally { sem.Release(); }
        });

        await Task.WhenAll(tasks);
    }
}
