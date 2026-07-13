using System.Text.Json;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>How a line was resolved from its enrichment result.</summary>
public enum LineEnrichmentRouting { AutoMatched, ReadyForReview, NeedsConfirmation, NeedsManual }

public sealed record EnrichmentApplyResult(LineEnrichmentRouting Routing, string? MatchedItemCode, string MatchStrategy);

public interface IEnrichmentResultRouter
{
    Task<EnrichmentApplyResult> ApplyAsync(Guid lineId, string? invoiceBrand, string? lineArticle, EnrichmentResponse enr, CancellationToken ct);
}

/// <summary>
/// Persists a DGX <c>/enrich_item</c> result on the line and decides its fate, enforcing supplier
/// identity (Slice 1.6 — a SAP item is exactly one supplier+article). Supplier identity is classified
/// FIRST, whether or not the donor already carries an <c>item_code</c> (Slice 2.1 — fresh Path E /
/// Germax donors start with <c>item_code=NULL</c>):
/// <list type="bullet">
///   <item>no usable data (failed / partial) → <c>needs_manual</c>;</item>
///   <item>same supplier AND donor already a SAP item → <b>auto-match</b> (Path C1);</item>
///   <item>same supplier but donor has no code yet → create-new ON the donor row (Path C2);</item>
///   <item>vehicle-group / unknown brand → <c>needs_confirmation</c> (operator picks use-existing / create-new / skip);</item>
///   <item>different specific supplier → create-new with borrowed enrichment, minting an own-identity
///         row (never link across suppliers, never write our code to the donor).</item>
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

    public async Task<EnrichmentApplyResult> ApplyAsync(Guid lineId, string? invoiceBrand, string? lineArticle, EnrichmentResponse enr, CancellationToken ct)
    {
        var source = enr.SourceLabel;
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

        // Donor row resolved — supplier identity decides what to do, EVEN when the donor has no SAP
        // code yet. Fresh Path E (RapidAPI) / Germax rows start with item_code=NULL, so classifying
        // only when item_code was present (the Slice 2 bug) let cross-supplier lines fall through to a
        // plain create-new and mint SAP items onto the wrong-supplier donor row (Slice 2.1 fix).
        if (donor is not null)
        {
            var donorHasItemCode = !string.IsNullOrWhiteSpace(donor.ItemCode);
            var kind = BrandClassifier.Classify(invoiceBrand, donor.SupplierName);

            // Identity is (supplier, article) — never the item_code (that is our generated primary key).
            // A borrowed_oem_bridge / rapidapi donor is reached via a shared OEM, so it is a DIFFERENT
            // article. Even when it is the SAME supplier, a different article is a different part: it must
            // create a NEW own-identity item (borrowing the donor's enrichment), NOT reuse the donor's
            // item_code (C1) nor write our code onto the donor row (C2). Without this gate, distinct Germax
            // GL#### SKUs sharing an LR OEM collapsed onto one existing item. (Cross-supplier and
            // vehicle-group/no-brand are handled by the switch below and already never reuse the donor.)
            if (kind == BrandClassifier.MatchKind.SameSupplier &&
                !ArticleEquals(donor.ArticleNumber, lineArticle))
            {
                var strategy = EnrichmentStrategies.ResolveSourceCrossSupplier(source);
                await Record(confirmationRequired: false, strategy);
                _logger.LogInformation(
                    "Line {LineId} same supplier '{Supplier}' but different article (line '{LineArticle}' vs donor '{DonorArticle}') → create-new own-identity via {Strategy}.",
                    lineId, donor.SupplierName, lineArticle, donor.ArticleNumber, strategy);
                return new EnrichmentApplyResult(LineEnrichmentRouting.ReadyForReview, null, strategy);
            }

            switch (kind)
            {
                // Same brand as the donor → the donor IS our row.
                case BrandClassifier.MatchKind.SameSupplier when donorHasItemCode:
                {
                    // C1 — donor is already a SAP item: direct auto-match.
                    var strategy = EnrichmentStrategies.ResolveSourceAutoMatch(source);
                    await Record(confirmationRequired: false, strategy);
                    await _review.SetReviewStatusAsync(lineId, "matched", donor.ItemCode, ct);
                    _logger.LogInformation("Line {LineId} auto-matched to {Code} via {Strategy} (same supplier {Supplier}).",
                        lineId, donor.ItemCode, strategy, donor.SupplierName);
                    return new EnrichmentApplyResult(LineEnrichmentRouting.AutoMatched, donor.ItemCode, strategy);
                }

                case BrandClassifier.MatchKind.SameSupplier:
                {
                    // C2 — donor exists for OUR brand but has no SAP code yet: Bulk Create writes the new
                    // item_code onto this donor row (no own-identity row needed — it's already ours).
                    var strategy = EnrichmentStrategies.ResolveSourceCreateNew(source);
                    await Record(enr.ConfirmationRequired, strategy);
                    _logger.LogInformation("Line {LineId} → create-new on same-supplier donor {OitmId} ({Supplier}) via {Strategy}.",
                        lineId, donor.Id, donor.SupplierName, strategy);
                    return new EnrichmentApplyResult(LineEnrichmentRouting.ReadyForReview, null, strategy);
                }

                case BrandClassifier.MatchKind.DifferentSupplier:
                {
                    // Different specific supplier — borrow the enrichment but mint a NEW own-identity SAP
                    // item; never link across suppliers and never write our code to the donor. Fires now
                    // regardless of donor.ItemCode (the Slice 2.1 fix for fresh Path E donors).
                    var strategy = EnrichmentStrategies.ResolveSourceCrossSupplier(source);
                    await Record(confirmationRequired: false, strategy);
                    _logger.LogInformation("Line {LineId} cross-supplier (brand '{Brand}' vs donor {Supplier}, donorHasItemCode={Has}) → create-new borrowed.",
                        lineId, invoiceBrand, donor.SupplierName, donorHasItemCode);
                    return new EnrichmentApplyResult(LineEnrichmentRouting.ReadyForReview, null, strategy);
                }

                case BrandClassifier.MatchKind.VehicleGroupBrand:
                case BrandClassifier.MatchKind.NoBrandOnInvoice:
                {
                    // Generic / missing invoice brand → operator decides (use donor's part or create new).
                    var strategy = EnrichmentStrategies.ResolveSourceNeedsConfirmation(source);
                    await Record(confirmationRequired: true, strategy);
                    await _review.SetNeedsConfirmationAsync(lineId, donor.ItemCode, donor.Id, donor.SupplierName, strategy, ct);
                    _logger.LogInformation("Line {LineId} → needs_confirmation: brand '{Brand}' vs donor {Code} ({Supplier}).",
                        lineId, invoiceBrand, donor.ItemCode, donor.SupplierName);
                    return new EnrichmentApplyResult(LineEnrichmentRouting.NeedsConfirmation, donor.ItemCode, strategy);
                }
            }
        }

        // No donor row at all (unknown oitm) → plain create-new (Path C2).
        var createStrategy = EnrichmentStrategies.ResolveSourceCreateNew(source);
        await Record(enr.ConfirmationRequired, createStrategy);
        return new EnrichmentApplyResult(LineEnrichmentRouting.ReadyForReview, null, createStrategy);
    }

    /// <summary>
    /// Same supplier article, case- and whitespace-insensitive. A blank on either side is treated as
    /// "not the same article" (conservative — we never reuse a donor's item_code, nor write our code onto
    /// a donor row, without a positive article match).
    /// </summary>
    private static bool ArticleEquals(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
