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
/// </summary>
public sealed class SkuGenerationService : ISkuGenerationService
{
    private readonly ISkuCounterRepository _counters;
    public SkuGenerationService(ISkuCounterRepository counters) => _counters = counters;

    public async Task<string> GenerateAsync(string prefix, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("SKU prefix is required.", nameof(prefix));

        var next = await _counters.IncrementAsync(prefix.Trim().ToUpperInvariant(), ct);
        return $"{prefix.Trim().ToUpperInvariant()}{next}";
    }
}
