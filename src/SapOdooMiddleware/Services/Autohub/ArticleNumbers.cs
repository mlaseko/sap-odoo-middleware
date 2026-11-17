using System.Text.RegularExpressions;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>Supplier article numbers compared the way people read them: letters and digits only, upper case.</summary>
public static class ArticleNumbers
{
    /// <summary>"b-12 698" → "B12698"; null when nothing is left.</summary>
    public static string? Normalize(string? article)
    {
        var s = Regex.Replace((article ?? "").ToUpperInvariant(), "[^A-Z0-9]", "");
        return s.Length == 0 ? null : s;
    }

    /// <summary>True only when both are known and differ (an unknown article never counts as a mismatch).</summary>
    public static bool Differ(string? a, string? b) =>
        Normalize(a) is { } x && Normalize(b) is { } y && x != y;
}
