using System.Text.Json;
using System.Text.Json.Serialization;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>Outcome of assigning a marque to one line. Kind maps to an HTTP status in the controllers.</summary>
public sealed record MarqueAssignResult(MarqueAssignKind Kind, string? Error, PartsReviewLineRow? Line)
{
    public bool Ok => Kind == MarqueAssignKind.Ok;
}

public enum MarqueAssignKind { Ok, BadRequest, NotFound, Conflict, Upstream, Failed }

public interface IMarqueAssignmentService
{
    /// <summary>
    /// Assign the operator's marque (manufacturer code) to one line: DGX finalizes the SKU prefix and item
    /// group under it, the package is merged into the stored enrichment, and the line is queued to create.
    /// An optional positive item group overrides DGX's group. Idempotent on the DGX side.
    /// </summary>
    Task<MarqueAssignResult> AssignAsync(
        Guid documentId, Guid lineId, string? manufacturerCode, int? itemsGroupCode, string requestedBy, CancellationToken ct);
}

/// <summary>
/// The per-line marque resolution, moved out of AutohubDocumentsController so the single-line endpoint and
/// the Issues bulk action run exactly the same steps.
/// </summary>
public sealed class MarqueAssignmentService : IMarqueAssignmentService
{
    private static readonly JsonSerializerOptions EnrichmentJson = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly IPartsReviewRepository _review;
    private readonly IManufacturerResolutionClient _manufacturer;

    public MarqueAssignmentService(IPartsReviewRepository review, IManufacturerResolutionClient manufacturer)
    {
        _review = review;
        _manufacturer = manufacturer;
    }

    public async Task<MarqueAssignResult> AssignAsync(
        Guid documentId, Guid lineId, string? manufacturerCode, int? itemsGroupCode, string requestedBy, CancellationToken ct)
    {
        var code = manufacturerCode?.Trim();
        if (string.IsNullOrWhiteSpace(code))
            return new(MarqueAssignKind.BadRequest, "manufacturerCode is required.", null);
        if (PartsItemProvisioningService.IsUnresolvedPrefix(code))
            return new(MarqueAssignKind.BadRequest, "'GEN' is not an assignable marque — pick a real manufacturer.", null);

        var row = await _review.GetByIdAsync(lineId, ct);
        if (row is null || row.DocumentId != documentId)
            return new(MarqueAssignKind.NotFound, "Line not found on this document.", null);
        // Resolvable from any actionable state — a needs_manufacturer hold OR during review (pending /
        // create_new / needs_manual). Only a terminal line (created / matched / skipped) is off-limits.
        if (row.ReviewStatus is "created" or "matched" or "skip")
            return new(MarqueAssignKind.Conflict, $"Line is '{row.ReviewStatus}' — nothing to resolve.", row);

        var payloadJson = await _review.GetEnrichmentPayloadAsync(lineId, ct);
        if (string.IsNullOrWhiteSpace(payloadJson))
            return new(MarqueAssignKind.Conflict, "Line has no stored enrichment to finalize.", row);

        EnrichmentResponse held;
        try
        {
            held = JsonSerializer.Deserialize<EnrichmentResponse>(payloadJson, EnrichmentJson)
                   ?? throw new InvalidOperationException("empty payload");
        }
        catch (Exception ex)
        {
            return new(MarqueAssignKind.Failed, $"Stored enrichment could not be read: {ex.Message}", row);
        }

        // OEMs DGX re-ranks under the marque: the line's own OEMs plus the enrichment's, de-duped. Brand is
        // the staging line Brand — the key the stored ruling is filed under.
        var oems = (row.OemNumbers ?? new List<string>())
            .Concat(held.ItemData?.FilteredOems ?? Enumerable.Empty<string>())
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ManufacturerPackage pkg;
        try
        {
            pkg = await _manufacturer.ResolveAsync(
                new ResolveManufacturerRequest(row.SupplierArticleNumber, row.Brand, code!, requestedBy, oems), ct);
        }
        catch (Exception ex)
        {
            return new(MarqueAssignKind.Upstream, $"Manufacturer resolution failed: {ex.Message}", row);
        }

        if (!string.IsNullOrWhiteSpace(pkg.Error))
            return new(MarqueAssignKind.Upstream, $"DGX could not resolve '{code}': {pkg.Error}", row);
        if (PartsItemProvisioningService.IsUnresolvedPrefix(pkg.Prefix))
            return new(MarqueAssignKind.Upstream, $"DGX returned no usable prefix for '{code}'.", row);

        var merged = ManufacturerResolutionMerge.Apply(held, pkg);
        // Optional operator override of the SAP item group: when positive it wins over DGX's donor group.
        if (itemsGroupCode is > 0 && merged.ItemData is { } md)
            merged = merged with { ItemData = md with { SuggestedItmsGrpCod = itemsGroupCode } };
        await _review.ApplyManufacturerResolutionAsync(lineId, JsonSerializer.Serialize(merged), ct);
        return new(MarqueAssignKind.Ok, null, await _review.GetByIdAsync(lineId, ct));
    }
}
