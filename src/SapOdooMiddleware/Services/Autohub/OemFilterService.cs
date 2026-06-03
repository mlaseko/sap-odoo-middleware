using System.Text.RegularExpressions;

namespace SapOdooMiddleware.Services.Autohub;

public sealed record OemFilterResult
{
    public required IReadOnlyList<string> CleanOems { get; init; }
    public required IReadOnlyList<string> NoiseFiltered { get; init; }
    public required string Source { get; init; }   // "regex_blacklist" | "all_filtered"
}

public interface IOemFilterService
{
    OemFilterResult Filter(IReadOnlyList<string> rawOems, string? supplierArticleNumber, string? brand);
}

/// <summary>
/// Cleans the noisy OEM cells that come off spare-parts invoices, separating real part-number
/// tokens from position descriptors ("Rear", "Front Right") and engine tokens ("3.0L", "V8").
///
/// This is the pure, DB-free <b>Option C</b> (regex + blacklist) half of decision D9. The
/// <b>Option 1</b> half (prefer Germax scraped data as the source of truth for LR items) needs a
/// Neon/DGX lookup and therefore lives in the slice-2 EnrichmentService, which calls this service
/// for the regex fallback. Registered as a singleton — no state, no I/O.
/// </summary>
public sealed class OemFilterService : IOemFilterService
{
    private static readonly HashSet<string> NoiseBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Rear", "Front", "Left", "Right", "Non Electrical",
        "Front Right", "Front Left", "Rear Right", "Rear Left",
        "Upper", "Lower", "Inner", "Outer",
        "Petrol", "Diesel", "Hybrid",
        "V6", "V8", "V12", "L4", "L6"
    };

    // "3.0L", "5L", "3.6L" etc.
    private static readonly Regex EngineSizePattern = new(
        @"^\d+(\.\d+)?L$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 4–20 chars, uppercase alphanumerics plus dash/space, starting alphanumeric.
    private static readonly Regex OemTokenPattern = new(
        @"^[A-Z0-9][A-Z0-9\-\s]{3,19}$", RegexOptions.Compiled);

    public OemFilterResult Filter(IReadOnlyList<string> rawOems, string? supplierArticleNumber, string? brand)
    {
        var clean = new List<string>();
        var noise = new List<string>();

        foreach (var raw in rawOems ?? Array.Empty<string>())
        {
            var token = raw?.Trim() ?? "";
            if (token.Length == 0)
                continue;

            if (NoiseBlacklist.Contains(token) || EngineSizePattern.IsMatch(token))
                noise.Add(token);
            else if (OemTokenPattern.IsMatch(token) && token.Any(char.IsDigit))
                clean.Add(token);
            else
                noise.Add(token);
        }

        return new OemFilterResult
        {
            CleanOems = clean,
            NoiseFiltered = noise,
            Source = clean.Count > 0 ? "regex_blacklist" : "all_filtered"
        };
    }

    /// <summary>True for Germax-style suppliers whose scraped data is the OEM source of truth (Option 1).</summary>
    public static bool IsGermaxBrand(string? brand) =>
        brand is not null &&
        (brand.Contains("GERMAX", StringComparison.OrdinalIgnoreCase) ||
         brand.Contains("GAPC", StringComparison.OrdinalIgnoreCase));
}
