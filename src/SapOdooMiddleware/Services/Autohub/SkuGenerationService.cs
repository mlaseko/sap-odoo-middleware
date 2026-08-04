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
    private readonly ISapSkuCounterRefreshService? _refresh;

    // _refresh is optional so unit tests can construct with just the counter repo; DI always supplies it.
    public SkuGenerationService(ISkuCounterRepository counters, ISapSkuCounterRefreshService? refresh = null)
    {
        _counters = counters;
        _refresh = refresh;
    }

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
        long next;
        try
        {
            next = await _counters.IncrementAsync(canonical, ct);
        }
        catch (InvalidOperationException) when (_refresh is not null)
        {
            // Prefix not seeded (e.g. the generic 'GEN' fallback on first use) — seed it from the live
            // SAP MAX, then retry once. If the seed still fails, the retry rethrows the same clear error.
            await _refresh.EnsureSeededAsync(canonical, ct);
            next = await _counters.IncrementAsync(canonical, ct);
        }
        return $"{canonical}{next}";
    }

    private static string Canonicalize(string prefix)
    {
        var p = prefix.Trim().ToUpperInvariant();
        return PrefixCorrections.TryGetValue(p, out var corrected) ? corrected : p;
    }
}
