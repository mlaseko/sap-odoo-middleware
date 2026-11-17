namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// The Autohub SAP ItemName: up to five OEM numbers, then the supplier article, then any alternate Germax
/// article numbers that still fit, joined by '/'. Every number is written without spaces
/// ("17 51 7 600 530" → "17517600530"); dashes and dots are kept ("A2053230965-SK").
/// </summary>
public static class ItemNames
{
    public const int MaxLength = 200;
    public const int MaxOems = 5;

    /// <summary>A part number as the ItemName writes it: without spaces (dashes and dots stay).</summary>
    public static string Regularize(string? number) =>
        new string((number ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>
    /// The key two spellings of one OEM number share: without spaces, upper case, without the Mercedes "A" before the
    /// digits and without leading zeros. "A 642 154 02 02", "642 154 02 02" and "A6421540402" are one
    /// number; so are "6D 109 601 M" and "06D109601M".
    /// </summary>
    internal static string SameNumberKey(string regularized)
    {
        var k = regularized.ToUpperInvariant();
        if (k.Length > 1 && k[0] == 'A' && char.IsDigit(k[1])) k = k[1..];
        var trimmed = k.TrimStart('0');
        return trimmed.Length > 0 ? trimmed : k;
    }

    /// <summary>
    /// The invoice line's OEMs first, then the catalogue card's, regularized and with each number only
    /// once (the first spelling seen is kept, so the invoice's wins).
    /// </summary>
    public static List<string> MergeOems(IEnumerable<string>? lineOems, IEnumerable<string>? cardOems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<string>();
        foreach (var o in (lineOems ?? Enumerable.Empty<string>()).Concat(cardOems ?? Enumerable.Empty<string>()))
        {
            var r = Regularize(o);
            if (r.Length > 0 && seen.Add(SameNumberKey(r))) merged.Add(r);
        }
        return merged;
    }

    /// <summary>
    /// OEMs (already merged, at most five used) + article + alternates. The OEM chain and the article are
    /// always kept (hard-capped at 200); alternates are added only while the whole name fits.
    /// </summary>
    public static string Build(IReadOnlyList<string> oems, string article, IReadOnlyList<string>? alternateArticles = null)
    {
        var primary = Regularize(article);
        var parts = oems.Select(Regularize).Where(o => o.Length > 0 && o != primary).Take(MaxOems).ToList();
        parts.Add(primary);
        var name = string.Join("/", parts);
        if (name.Length > MaxLength) return name[..MaxLength];

        var used = new HashSet<string>(parts, StringComparer.Ordinal);
        foreach (var alt in alternateArticles ?? Enumerable.Empty<string>())
        {
            var a = Regularize(alt);
            if (a.Length == 0 || !used.Add(a)) continue;
            var candidate = name + "/" + a;
            if (candidate.Length > MaxLength) break;
            name = candidate;
        }
        return name;
    }
}
