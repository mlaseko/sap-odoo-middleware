using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Bulk-creates SAP items for every Autohub line marked 'create_new'. Sequential (the DI API does
/// not tolerate concurrent writes), continues past individual failures, and applies a per-item
/// timeout so one slow enrich/create can't hang the batch. Per-line outcomes are persisted by the
/// provisioning service. Mirrors the Lubes InvoiceItemCreationService.
///
/// Price review (parts_price_review): a line approved in the price review is created at its approved
/// PL03 (PL05 re-derived); a line held there is not created. A line with no current decision (none,
/// or one whose figures changed since) is created at the calculated price, unless
/// AutohubPricing:RequirePriceReview is on, in which case it is left to create until approved.
/// </summary>
public sealed class PartsItemCreationService
{
    private static readonly TimeSpan PerItemTimeout = TimeSpan.FromSeconds(120);

    private readonly IPartsItemProvisioningService _provisioning;
    private readonly IPartsReviewRepository _review;
    private readonly IStagingPartsDocumentRepository _docs;
    private readonly IPriceReviewService _priceReview;
    private readonly AutohubPricingSettings _pricingSettings;
    private readonly ILogger<PartsItemCreationService> _logger;
    private readonly IInvoiceActivityLog? _activity;   // per-line outcome in the invoice history

    public PartsItemCreationService(
        IPartsItemProvisioningService provisioning,
        IPartsReviewRepository review,
        IStagingPartsDocumentRepository docs,
        IPriceReviewService priceReview,
        IOptions<AutohubPricingSettings> pricingSettings,
        ILogger<PartsItemCreationService> logger,
        IInvoiceActivityLog? activity = null)
    {
        _activity = activity;
        _provisioning = provisioning;
        _review = review;
        _docs = docs;
        _priceReview = priceReview;
        _pricingSettings = pricingSettings.Value;
        _logger = logger;
    }

    public async Task<PartsBulkCreateResult> BulkCreateAsync(
        Guid documentId, CancellationToken ct,
        IReadOnlyList<BulkCreateLineSelection>? selections = null)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        var currency = doc?.Currency;

        var toCreate = await _review.ListCreateNewAsync(documentId, ct);

        int created = 0, needsConfirmation = 0, held = 0, notPriceApproved = 0, priceHeld = 0;
        var failures = new List<PartsBulkCreateFailure>();

        // Price review decisions, read with the current figures so an approval whose figures changed
        // (stale) does not count. Read before any SAP write: if this fails, nothing is created.
        var required = _pricingSettings.RequirePriceReview;
        var review = await _priceReview.GetReviewAsync(documentId, ct);
        var reviewed = review?.Lines.ToDictionary(l => l.LineId) ?? new Dictionary<Guid, PriceReviewLine>();

        // The price review gate posts a specific list of reviewed lines (with optional
        // per-line price overrides); legacy callers send no list and get every eligible
        // line, exactly as before. A requested id that is not eligible fails that line
        // only — the reviewed batch is never silently widened or narrowed.
        List<(PartsProvisioningLine Line, PartsLineOverride? Override)> work;
        if (selections is null)
        {
            work = new List<(PartsProvisioningLine, PartsLineOverride?)>(toCreate.Count);
            foreach (var line in toCreate)
            {
                var (action, priceOverride) = ApplyPriceReview(reviewed.GetValueOrDefault(line.Id), required);
                switch (action)
                {
                    case PriceReviewAction.Create: work.Add((line, priceOverride)); break;
                    case PriceReviewAction.Held: priceHeld++; break;
                    default: notPriceApproved++; break;
                }
            }
        }
        else
        {
            var eligible = toCreate.ToDictionary(l => l.Id);
            work = new List<(PartsProvisioningLine, PartsLineOverride?)>(selections.Count);
            foreach (var s in selections)
            {
                if (!eligible.TryGetValue(s.LineId, out var line))
                    failures.Add(new PartsBulkCreateFailure(s.LineId, null,
                        "Line not found on this document or not eligible (ReviewStatus must be create_new/create_failed)."));
                else if (required && ApplyPriceReview(reviewed.GetValueOrDefault(s.LineId), true).Action != PriceReviewAction.Create)
                    failures.Add(new PartsBulkCreateFailure(s.LineId, line.SupplierArticleNumber,
                        "Line is not approved in the price review (AutohubPricing:RequirePriceReview is on)."));
                else
                    work.Add((line, s.PriceOverride));
            }
        }

        var createdLines = new List<ActivityLine>();
        foreach (var (line, priceOverride) in work)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerItemTimeout);

                var outcome = await _provisioning.ProvisionAsync(line, currency, timeoutCts.Token, priceOverride);
                switch (outcome.Status)
                {
                    case "created":
                        created++;
                        createdLines.Add(new ActivityLine(line.Id, $"Created in SAP as {outcome.ItemCode}" +
                            (priceOverride?.Pl03Override is { } pl03 ? $" at the approved PL03 {pl03:N0}." : ".")));
                        break;
                    case "needs_confirmation": needsConfirmation++; break;
                    // Holds are waiting on an operator decision, NOT failures — the provisioning service has
                    // already parked the line with its own status/reason; just tally them apart.
                    case "needs_manufacturer":
                    case "prefix_exhausted": held++; break;
                    default: failures.Add(new PartsBulkCreateFailure(line.Id, line.SupplierArticleNumber, outcome.Error ?? "failed")); break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // host shutdown — abort the batch
            }
            catch (OperationCanceledException)
            {
                await _review.RecordCreateFailedAsync(line.Id, $"Timed out after {PerItemTimeout.TotalSeconds:N0}s.", ct);
                failures.Add(new PartsBulkCreateFailure(line.Id, line.SupplierArticleNumber, "timed out"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk-create failed for line {LineId} (article {Article}).", line.Id, line.SupplierArticleNumber);
                await _review.RecordCreateFailedAsync(line.Id, ex.Message, ct);
                failures.Add(new PartsBulkCreateFailure(line.Id, line.SupplierArticleNumber, ex.Message));
            }
        }

        if (_activity is not null)
        {
            if (createdLines.Count > 0)
                await _activity.RecordLinesAsync(documentId, "created", "Created in SAP.", "bulk create", createdLines, ct);
            if (failures.Count > 0)
                await _activity.RecordLinesAsync(documentId, "create_failed", "Creating the SAP item failed.", "bulk create",
                    failures.Select(f => new ActivityLine(f.LineId, "Creating the SAP item failed: " + f.Error)).ToList(), ct);
        }

        var attempted = selections?.Count ?? work.Count;
        _logger.LogInformation(
            "Autohub bulk-create for {Id}: attempted {Attempted}, created {Created}, needsConfirmation {Needs}, held {Held}, failed {Failed}, not price-approved {NotApproved}, held in price review {PriceHeld} (review required: {Required}).",
            documentId, attempted, created, needsConfirmation, held, failures.Count, notPriceApproved, priceHeld, required);

        return new PartsBulkCreateResult(attempted, created, needsConfirmation, held, failures.Count, failures,
            notPriceApproved, priceHeld);
    }

    internal enum PriceReviewAction { Create, Held, NotApproved }

    /// <summary>
    /// What Bulk Create does with a line given its price review state. A current approval creates the
    /// line at its approved PL03 (an override only when it differs from the calculated PL03, so an
    /// unchanged price leaves no override record); a current hold skips it; no current decision
    /// creates it at the calculated price, or skips it when review is required.
    /// </summary>
    internal static (PriceReviewAction Action, PartsLineOverride? Override) ApplyPriceReview(
        PriceReviewLine? line, bool required)
    {
        var d = line?.Decision;
        if (d is null || d.Stale)
            return required ? (PriceReviewAction.NotApproved, null) : (PriceReviewAction.Create, null);
        if (d.Status == "held")
            return (PriceReviewAction.Held, null);
        if (d.Status != "approved")
            return required ? (PriceReviewAction.NotApproved, null) : (PriceReviewAction.Create, null);

        var changed = d.FinalPl03 is { } final && line!.FormulaPl03 is { } formula && final != formula;
        return changed
            ? (PriceReviewAction.Create, new PartsLineOverride(null, null, d.FinalPl03, d.Reason, d.ReviewedBy))
            : (PriceReviewAction.Create, null);
    }
}

/// <summary>One reviewed line the gate asks Bulk Create to post, with its price decision.</summary>
public sealed record BulkCreateLineSelection(Guid LineId, PartsLineOverride? PriceOverride);

/// <summary>
/// NotPriceApproved: lines left to create because they have no current price approval and
/// RequirePriceReview is on. PriceHeld: lines left to create because they are held in the price review.
/// </summary>
public record PartsBulkCreateResult(
    int Attempted, int Created, int NeedsConfirmation, int Held, int Failed, List<PartsBulkCreateFailure> Failures,
    int NotPriceApproved = 0, int PriceHeld = 0);
public record PartsBulkCreateFailure(Guid LineId, string? ArticleNumber, string Error);
