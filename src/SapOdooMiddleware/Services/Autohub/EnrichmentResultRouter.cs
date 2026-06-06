using System.Text.Json;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>How a line was resolved from its enrichment result.</summary>
public enum LineEnrichmentRouting { AutoMatched, ReadyForReview, NeedsConfirmation, NeedsManual }

public sealed record EnrichmentApplyResult(LineEnrichmentRouting Routing, string? MatchedItemCode, string MatchStrategy);

public interface IEnrichmentResultRouter
{
    Task<EnrichmentApplyResult> ApplyAsync(Guid lineId, string? invoiceBrand, EnrichmentResponse enr, CancellationToken ct);
}

/// <summary>
/// Persists a DGX <c>/enrich_item</c> result on the line and decides its fate, enforcing supplier
/// identity (Slice 1.6 — a SAP item is exactly one supplier+article):
/// <list type="bullet">
///   <item>no usable data (failed / partial) → <c>needs_manual</c>;</item>
///   <item>donor already a SAP item AND same supplier → <b>auto-match</b> (Path C1);</item>
///   <item>donor already a SAP item BUT a vehicle-group / unknown brand → <c>needs_confirmation</c>
///         (operator picks use-existing / create-new / skip);</item>
///   <item>donor already a SAP item BUT a different supplier → create-new with borrowed enrichment
///         (never link across suppliers);</item>
///   <item>donor not yet a SAP item → ready for review (Path C2).</item>
/// </list>
/// Shared by the on-demand endpoint and the background worker so both route identically.
/// </summary>
public sealed class EnrichmentResultRouter : IEnrichmentResultRouter
{
    private readonly IPartsReviewRepository _review;
    private readonly INeonBridgeService _bridge;
    private readonly ILogger<EnrichmentResultRouter> _logger;

    public EnrichmentResultRouter(IPartsReviewRepository review, INeonBridgeService bridge, ILogger<EnrichmentResultRouter> logger)
    {
        _review = review;
        _bridge = bridge;
        _logger = logger;
    }

    public async Task<EnrichmentApplyResult> ApplyAsync(Guid lineId, string? invoiceBrand, EnrichmentResponse enr, CancellationToken ct)
    {
        var source = enr.SourceLabel;
        var borrowed = string.Equals(source, "borrowed_oem_bridge", StringComparison.OrdinalIgnoreCase);
        var status = enr.Status ?? (enr.ItemData is null ? "partial" : "success");
        var payload = JsonSerializer.Serialize(enr);

        async Task Record(bool confirmationRequired, string matchStrategy) =>
            await _review.RecordEnrichmentResultAsync(lineId, source, enr.BorrowedFrom?.ArticleNumber,
                enr.BorrowedFrom?.SupplierName, enr.NeonOitmId, confirmationRequired, status,
                enr.Error?.Code, matchStrategy, payload, ct);

        // No usable enrichment (hard failure or partial/unmatched) → needs_manual.
        if (enr.IsFailed || enr.ItemData is null)
        {
            await Record(false, "unmatched");
            await _review.SetReviewStatusAsync(lineId, "needs_manual", null, ct);
            return new EnrichmentApplyResult(LineEnrichmentRouting.NeedsManual, null, "unmatched");
        }

        // Look up the donor parts_catalog row to learn whether it is already a SAP item and whose.
        OitmRow? donor = null;
        if (enr.NeonOitmId is { } oitmId)
        {
            try { donor = await _bridge.GetOitmRowAsync(oitmId, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Donor lookup failed for oitm {OitmId}; treating as create-new.", oitmId); }
        }

        // Donor already maps to a SAP item — supplier identity decides what to do.
        if (donor is not null && !string.IsNullOrWhiteSpace(donor.ItemCode))
        {
            switch (BrandClassifier.Classify(invoiceBrand, donor.SupplierName))
            {
                case BrandClassifier.MatchKind.SameSupplier:
                {
                    var strategy = borrowed ? "borrowed_oem_bridge_auto_match" : "enrichment_direct_auto_match";
                    await Record(confirmationRequired: false, strategy);
                    await _review.SetReviewStatusAsync(lineId, "matched", donor.ItemCode, ct);
                    _logger.LogInformation("Line {LineId} auto-matched to {Code} via {Strategy} (same supplier {Supplier}).",
                        lineId, donor.ItemCode, strategy, donor.SupplierName);
                    return new EnrichmentApplyResult(LineEnrichmentRouting.AutoMatched, donor.ItemCode, strategy);
                }

                case BrandClassifier.MatchKind.VehicleGroupBrand:
                case BrandClassifier.MatchKind.NoBrandOnInvoice:
                {
                    const string strategy = "vehicle_group_brand_needs_confirmation";
                    await Record(confirmationRequired: true, strategy);
                    await _review.SetNeedsConfirmationAsync(lineId, donor.ItemCode, donor.Id, donor.SupplierName, strategy, ct);
                    _logger.LogInformation("Line {LineId} → needs_confirmation: brand '{Brand}' vs donor {Code} ({Supplier}).",
                        lineId, invoiceBrand, donor.ItemCode, donor.SupplierName);
                    return new EnrichmentApplyResult(LineEnrichmentRouting.NeedsConfirmation, donor.ItemCode, strategy);
                }

                case BrandClassifier.MatchKind.DifferentSupplier:
                {
                    // Different supplier — borrow the enrichment but mint a NEW SAP item; never link across suppliers.
                    const string strategy = "borrowed_cross_supplier_create_new";
                    await Record(confirmationRequired: false, strategy);
                    _logger.LogInformation("Line {LineId} cross-supplier (brand '{Brand}' vs donor {Supplier}) → create-new borrowed.",
                        lineId, invoiceBrand, donor.SupplierName);
                    return new EnrichmentApplyResult(LineEnrichmentRouting.ReadyForReview, null, strategy);
                }
            }
        }

        // Donor not yet a SAP item (or unknown) → usual create-new path (Path C2).
        var createStrategy = borrowed ? "borrowed_oem_bridge_create_new" : "enrichment_direct";
        await Record(enr.ConfirmationRequired, createStrategy);
        return new EnrichmentApplyResult(LineEnrichmentRouting.ReadyForReview, null, createStrategy);
    }
}
