namespace MolasLubes.Infrastructure.Integrations.LiquiMoly;

/// <summary>
/// Scraper settings for Meguin (a Liqui Moly subsidiary, invoiced under LM with "Meguin" in the line
/// name). Same Magento platform as Liqui Moly, so it reuses the whole scraper via
/// <see cref="MeguinProductScraperService"/>. Meguin's on-site search is broken (HTTP 500, like LM's
/// "/en"), so SKUs are resolved from the sitemap; the catalogue is small, so the one-time crawl is short
/// and is persisted like the LM index. Bound to the "Meguin" configuration section; the defaults below
/// apply when that section (or a field) is absent.
/// </summary>
public sealed class MeguinScraperSettings : LiquiMolyScraperSettings
{
    public MeguinScraperSettings()
    {
        BaseUrl = "https://www.meguin.com";

        // Sitemap-driven: the sitemap is the complete product list, and variant-mining each product page
        // yields every SKU. No category crawl needed (leave CategoryPaths empty).
        CategoryPaths = new();
        UseSitemap    = true;
        SitemapUrls   = new() { "https://www.meguin.com/sitemap/www.meguin.com/sitemap_en.xml" };

        // Persist outside the app folder so redeploys don't wipe it (same convention as the LM cache).
        IndexCachePath = @"C:\SapOdoo\Cache\meguin-index.json";

        // Small catalogue: a low floor (so a healthy build isn't rejected) and a generous-enough budget.
        MinIndexSkuCount = 20;
        MineMaxMinutes   = 20;
    }
}
