using System.Text.Json;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>How a line was resolved from its enrichment result.</summary>
public enum LineEnrichmentRouting { AutoMatched, ReadyForReview, NeedsManual }

public sealed record EnrichmentApplyResult(LineEnrichmentRouting Routing, string? MatchedItemCode, string MatchStrategy);

public interface IEnrichmentResultRouter
{
    Task<EnrichmentApplyResult> ApplyAsync(Guid lineId, EnrichmentResponse enr, CancellationToken ct);
}

/// <summary>
/// Persists a DGX <c>/enrich_item</c> result on the line and decides its fate (Slice-1 Path C1/C2):
/// <list type="bullet">
///   <item>no usable data (failed / partial) → <c>needs_manual</c> (never silently dropped);</item>
///   <item>donor <c>oitm</c> already carries a SAP item_code → <b>auto-match</b> to it (Path C1; no
///         creation, no confirmation modal — avoids a duplicate SAP item and a clobbered bridge link);</item>
///   <item>otherwise → ready for review (operator confirms → <c>create_new</c>, Path C2).</item>
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

    public async Task<EnrichmentApplyResult> ApplyAsync(Guid lineId, EnrichmentResponse enr, CancellationToken ct)
    {
        var source = enr.SourceLabel;
        var borrowed = string.Equals(source, "borrowed_oem_bridge", StringComparison.OrdinalIgnoreCase);
        var status = enr.Status ?? (enr.ItemData is null ? "partial" : "success");
        var payload = JsonSerializer.Serialize(enr);

        // No usable enrichment (hard failure or partial/unmatched) → needs_manual.
        if (enr.IsFailed || enr.ItemData is null)
        {
            await _review.RecordEnrichmentResultAsync(lineId, source, enr.BorrowedFrom?.ArticleNumber,
                enr.BorrowedFrom?.SupplierName, enr.NeonOitmId, enr.ConfirmationRequired, status,
                enr.Error?.Code, "unmatched", payload, ct);
            await _review.SetReviewStatusAsync(lineId, "needs_manual", null, ct);
            return new EnrichmentApplyResult(LineEnrichmentRouting.NeedsManual, null, "unmatched");
        }

        // Path C1: the donor parts_catalog row is ALREADY a SAP item — auto-match, don't create.
        string? existingCode = null;
        if (enr.NeonOitmId is { } oitmId)
        {
            try
            {
                existingCode = await _bridge.GetItemCodeAsync(oitmId, ct);
            }
            catch (Exception ex)
            {
                // A lookup hiccup must not block review — fall through to the create path.
                _logger.LogWarning(ex, "C1 item_code lookup failed for oitm {OitmId}; treating line as create-new.", oitmId);
            }
        }

        if (!string.IsNullOrWhiteSpace(existingCode))
        {
            var strategy = borrowed ? "borrowed_oem_bridge_auto_match" : "enrichment_direct_auto_match";
            // Persist enrichment (confirmation no longer required — it's a match, not a create) + match.
            await _review.RecordEnrichmentResultAsync(lineId, source, enr.BorrowedFrom?.ArticleNumber,
                enr.BorrowedFrom?.SupplierName, enr.NeonOitmId, confirmationRequired: false, status,
                enr.Error?.Code, strategy, payload, ct);
            await _review.SetReviewStatusAsync(lineId, "matched", existingCode, ct);
            _logger.LogInformation(
                "Line {LineId} auto-matched to existing SAP item {Code} via {Strategy} (oitm {OitmId}).",
                lineId, existingCode, strategy, enr.NeonOitmId);
            return new EnrichmentApplyResult(LineEnrichmentRouting.AutoMatched, existingCode, strategy);
        }

        // Path C2 / direct create: usable enrichment, donor not yet in SAP — operator confirms + creates.
        var createStrategy = borrowed ? "borrowed_oem_bridge_create_new" : "enrichment_direct";
        await _review.RecordEnrichmentResultAsync(lineId, source, enr.BorrowedFrom?.ArticleNumber,
            enr.BorrowedFrom?.SupplierName, enr.NeonOitmId, enr.ConfirmationRequired, status,
            enr.Error?.Code, createStrategy, payload, ct);
        return new EnrichmentApplyResult(LineEnrichmentRouting.ReadyForReview, null, createStrategy);
    }
}
