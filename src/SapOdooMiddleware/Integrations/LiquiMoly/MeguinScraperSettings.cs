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

        // Crawl Meguin's category pages (slugs from its sitemap) so each product gets a Category hint for
        // DGX — same as Liqui Moly. The sitemap is still used (UseSitemap) to variant-mine orphans not
        // surfaced under any category. Non-existent slugs simply log "empty" and are skipped.
        CategoryPaths = new()
        {
            { "/en/engine-oils.html",                 "Engine Oils"                  },
            { "/en/gear-oils.html",                   "Gear Oils"                    },
            { "/en/additives.html",                   "Additives"                    },
            { "/en/vehicle-care.html",                "Vehicle Care"                 },
            { "/en/service-products.html",            "Service Products"             },
            { "/en/greases.html",                     "Greases"                      },
            { "/en/pastes.html",                      "Pastes"                       },
            { "/en/workshop-pro-line.html",           "Workshop Pro-Line"            },
            { "/en/repair-aids-service-products.html", "Repair aids/service products" },
            { "/en/adhesives-sealants.html",          "Adhesives & Sealants"         },
        };
        UseSitemap    = true;
        SitemapUrls   = new() { "https://www.meguin.com/sitemap/www.meguin.com/sitemap_en.xml" };

        // Persist outside the app folder so redeploys don't wipe it (same convention as the LM cache).
        IndexCachePath = @"C:\SapOdoo\Cache\meguin-index.json";

        // Small catalogue: a low floor (so a healthy build isn't rejected) and a generous-enough budget.
        MinIndexSkuCount = 20;
        MineMaxMinutes   = 20;
    }
}
