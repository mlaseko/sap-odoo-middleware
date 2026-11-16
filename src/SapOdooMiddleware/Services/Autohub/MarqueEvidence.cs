using System.Text.RegularExpressions;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Which marque (SKU prefix family) a line's own data points to, independent of the DGX's manufacturer
/// vote: the vehicle makes in its fitment text, and the structure of its OEM numbers. Mirrors the DGX's
/// manufacturer_resolution.py (BRAND_TO_MARQUE and s4_oe_structure), with two differences: an OEM's
/// supplier suffix ("A4633307001-SK2") is ignored, so the number's structure is still recognised; and
/// BMW's 11-digit numbers are checked before the VAG pattern, which they also fit.
/// </summary>
public static class MarqueEvidence
{
    /// <summary>Marque prefixes in use: VAG, BM, MB, LR, MINI, FRD, VOL.</summary>
    private static readonly (string Pattern, string Marque)[] Makes =
    {
        (@"\bVOLKSWAGEN\b|\bVW\b|\bAUDI\b|\bSKODA\b|\bSEAT\b|\bPORSCHE\b|\bBENTLEY\b|\bCUPRA\b", "VAG"),
        (@"\bBMW\b", "BM"),
        (@"\bMINI\b", "MINI"),
        (@"\bMERCEDES\b|\bMERCEDES-BENZ\b|\bDAIMLER\b|\bSMART\b", "MB"),
        (@"\bLAND ROVER\b|\bLAND-ROVER\b|\bRANGE ROVER\b|\bJAGUAR\b", "LR"),
        (@"\bFORD\b", "FRD"),
        (@"\bVOLVO\b", "VOL"),
    };

    /// <summary>The marques of the vehicle makes named in a fitment text ("MERCEDES-BENZ SPRINTER …").</summary>
    public static IReadOnlySet<string> FromFitment(string? fitment)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(fitment)) return set;
        var t = fitment.ToUpperInvariant();
        foreach (var (pattern, marque) in Makes)
            if (Regex.IsMatch(t, pattern)) set.Add(marque);
        return set;
    }

    /// <summary>
    /// The marque an OEM number's structure belongs to, or null (same rules as the DGX's S4). Tried as written
    /// (VAG numbers are often spaced: "5Q0 121 251 EL"), then without a supplier suffix ("A4633307001-SK2"),
    /// then as its first word (a cell holding two numbers: "LR110708 LR079950").
    /// </summary>
    public static string? FromOem(string? oem)
    {
        if (string.IsNullOrWhiteSpace(oem)) return null;
        var t = oem.Trim();
        var noSuffix = Regex.Replace(t, @"[-\s]+[A-Za-z]{1,4}[0-9]?$", "");
        var firstWord = t.Split(new[] { ' ', '\t', '/' }, StringSplitOptions.RemoveEmptyEntries)[0];
        foreach (var candidate in new[] { t, noSuffix, firstWord })
            if (Structure(candidate) is { } m) return m;
        return null;
    }

    private static string? Structure(string value)
    {
        var n = Regex.Replace(value, "[^A-Za-z0-9]", "").ToUpperInvariant();
        if (n.Length == 0) return null;
        // BMW's 11-digit numbers and Mercedes' A-numbers go first: an 11-digit number also fits the VAG
        // pattern below, so the DGX's order (VAG first) reads BMW numbers such as 11427953129 as VAG.
        if (Regex.IsMatch(n, "^[0-9]{11}$")) return "BM";
        if (Regex.IsMatch(n, "^A[0-9]{10}$")) return "MB";
        if (Regex.IsMatch(n, "^[0-9][A-Z0-9]{2}[0-9]{3}[0-9A-Z]{3,5}$") && "01234589".Contains(n[0])) return "VAG";
        if (Regex.IsMatch(n, "^WHT[0-9]{6}")) return "VAG";
        if (Regex.IsMatch(n, "^[0-9]{10}$") && n[0] is '1' or '2') return "MB";
        if (Regex.IsMatch(n, "^(LR|JDE|CPLA|DPLA|SDB|RNB|RBJ|RBK|TDB|TVB|STC|ERR|NTC)[0-9]")) return "LR";
        return null;
    }

    /// <summary>Every marque the line's fitment and OEM numbers point to.</summary>
    public static IReadOnlySet<string> For(string? fitment, IEnumerable<string>? oems)
    {
        var set = new HashSet<string>(FromFitment(fitment), StringComparer.OrdinalIgnoreCase);
        foreach (var raw in oems ?? Enumerable.Empty<string>())
            foreach (var o in Regex.Split(raw ?? "", @"[,;]+"))
                if (FromOem(o) is { } m) set.Add(m);
        return set;
    }

    /// <summary>Plain-language name of a marque prefix.</summary>
    public static string Label(string? prefix) => (prefix ?? "").ToUpperInvariant() switch
    {
        "VAG" => "VW / Audi / Porsche", "BM" => "BMW", "MB" => "Mercedes-Benz", "LR" => "Land Rover / Jaguar",
        "MINI" => "MINI", "FRD" => "Ford", "VOL" => "Volvo", _ => prefix ?? "?",
    };
}
