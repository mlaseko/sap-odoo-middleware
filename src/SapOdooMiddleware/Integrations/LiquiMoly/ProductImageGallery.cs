using System.Text.RegularExpressions;

namespace MolasLubes.Infrastructure.Integrations.LiquiMoly;

/// <summary>
/// The pictures of one article, for people to look at (the Odoo inquiry gallery). A Liqui Moly product page lists
/// each picture several times (thumbnails of 100 to 800 px, through the cloudimg CDN and directly) and the pictures of
/// every pack of the product (21130 1 l, 21131 5 l, 21132 20 l…). This keeps:
///   - the article's own pictures (file names start with "{sku}_"), when the page has any;
///   - each picture once, at its original size (the catalogue file without cache folder, CDN or resize query);
///   - the PIM article image only when the page has no picture of the article (it repeats the front, at ~3 MB);
///   - the order given (the scraper's: best first, then page order, so the front of the pack leads), at most
///     <paramref name="max"/>.
/// </summary>
public static class ProductImageGallery
{
    private static readonly Regex CatalogPath = new(
        @"/media/catalog/product/(?:cache/[0-9a-f]+/)?(?<dir>[^/?#]/[^/?#]/)(?<file>[^/?#]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PimPath = new(
        @"pim\.liqui-moly\.de/ws/media/article-image/(?<sku>[0-9A-Za-z-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LeadingSku = new(@"^(?<sku>\d+)_", RegexOptions.Compiled);

    public static List<string> Clean(IEnumerable<string?>? urls, string sku, int max = 6, string host = "https://www.liqui-moly.com")
    {
        var catalog = new List<(string Key, string Url, string? Sku)>();
        var pim = new List<(string Url, string Sku)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in urls ?? Enumerable.Empty<string?>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (CatalogPath.Match(raw) is { Success: true } m)
            {
                var file = m.Groups["file"].Value;
                if (!seen.Add("c:" + file)) continue;
                var fileSku = LeadingSku.Match(file) is { Success: true } s ? s.Groups["sku"].Value : null;
                catalog.Add((file, $"{host.TrimEnd('/')}/media/catalog/product/{m.Groups["dir"].Value}{file}", fileSku));
            }
            else if (PimPath.Match(raw) is { Success: true } p)
            {
                var id = p.Groups["sku"].Value;
                if (!seen.Add("p:" + id)) continue;
                pim.Add((raw.Trim(), id));
            }
        }

        bool Mine(string? s) => string.Equals(s, sku?.Trim(), StringComparison.OrdinalIgnoreCase);
        var own = catalog.Where(c => Mine(c.Sku)).Select(c => c.Url).ToList();
        if (own.Count > 0)
            return own.Take(max).ToList();

        var ownPim = pim.Where(p => Mine(p.Sku)).Select(p => p.Url).ToList();
        if (ownPim.Count > 0)
            return ownPim.Take(max).ToList();

        // nothing named after the article: the page's pictures as they are (deduplicated)
        return catalog.Select(c => c.Url).Concat(pim.Select(p => p.Url)).Take(max).ToList();
    }
}
