using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

public interface ISkuGenerationService
{
    /// <summary>Atomically allocates the next ItemCode for a prefix, e.g. "LR" → "LR100601".</summary>
    Task<string> GenerateAsync(string prefix, CancellationToken ct);
}

/// <summary>
/// Allocates the next SAP ItemCode for a brand prefix via the atomic sku_counters increment.
/// The increment burns a number even if the subsequent SAP write fails — callers MUST run this
/// inside the same transaction as the OITM write (see SapItemProvisioningService, slice 3) so a
/// failed write rolls the counter back. Formats as prefix + value with no separator.
///
/// Counters are auto-refreshed from the live SAP MAX by SapSkuCounterRefreshService (startup +
/// daily + on-demand), so they are never manually seeded — they self-heal towards SAP.
/// </summary>
public sealed class SkuGenerationService : ISkuGenerationService
{
    private readonly ISkuCounterRepository _counters;
    public SkuGenerationService(ISkuCounterRepository counters) => _counters = counters;

    /// <summary>
    /// Canonical SAP prefix corrections. The SAP convention for MINI is the 4-character "MINI", not
    /// the 3-character "MIN" some upstream sources truncate to; normalise so the counter key and the
    /// generated ItemCode both use "MINI". Other brands pass through unchanged.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PrefixCorrections =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MIN"]  = "MINI",
            ["MINI"] = "MINI",
        };

    public async Task<string> GenerateAsync(string prefix, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("SKU prefix is required.", nameof(prefix));

        var canonical = Canonicalize(prefix);
        var next = await _counters.IncrementAsync(canonical, ct);
        return $"{canonical}{next}";
    }

    private static string Canonicalize(string prefix)
    {
        var p = prefix.Trim().ToUpperInvariant();
        return PrefixCorrections.TryGetValue(p, out var corrected) ? corrected : p;
    }
}
