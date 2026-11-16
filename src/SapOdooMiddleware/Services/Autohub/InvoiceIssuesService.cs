using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

// ── What a line needs next ─────────────────────────────────────────────────────

/// <summary>One thing wrong with (or still to do on) a line.</summary>
public sealed record LineIssue(string Code, string Text);

/// <summary>
/// A line's place in the review. NextAction is the single most important thing to do (an issue code,
/// "ready" when it will be created as it is, or "created" / "matched" / "skipped" when done). Issues lists
/// every open issue, most important first.
/// </summary>
public sealed record LineNextAction(string NextAction, IReadOnlyList<LineIssue> Issues)
{
    public bool IsDone => NextAction is "created" or "matched" or "skipped";
}

/// <summary>
/// A line's price review state, for the "price" issue: approved | held | stale | none | error. Pl03 is the
/// price it would be created at: the approved PL03 when Pl03Approved, else the calculated one.
/// </summary>
public sealed record LinePriceState(string State, string? Error = null, decimal? Pl03 = null, bool Pl03Approved = false);

/// <summary>
/// Works out what each Autohub invoice line needs next. Pure: no I/O, so it is unit-tested directly.
///
/// Order of importance (the first that applies is the line's next action):
/// create_failed, fix_data, confirm_match, check_part, manual, awaiting_enrichment, assign_marque,
/// accept_marque, sku_range_full, confirm_borrow, queue, price, then ready. Choosing the part comes before
/// the marque because a different part can change the marque.
/// </summary>
public static class LineIssueClassifier
{
    public static readonly IReadOnlyList<string> Order = new[]
    {
        "create_failed", "fix_data", "confirm_match", "check_part", "manual", "awaiting_enrichment",
        "assign_marque", "accept_marque", "confirm_marque", "sku_range_full", "confirm_borrow", "queue", "price",
    };

    private static readonly Dictionary<string, int> Rank =
        Order.Select((code, i) => (code, i)).ToDictionary(x => x.code, x => x.i);

    /// <summary>
    /// The marque rules. The item is created under SkuPrefix; the DGX's manufacturer vote (MarqueResolved /
    /// MarqueResolvedPrefix) and the line's own evidence (fitment makes, OEM-number structure) check it.
    /// <list type="bullet">
    /// <item>No usable prefix (blank / GEN), or held for a marque: accept_marque when the DGX resolved one,
    /// otherwise assign_marque, suggesting the evidence's marque before the DGX's guess.</item>
    /// <item>A real prefix that the evidence or the DGX confirms: nothing to do.</item>
    /// <item>A real prefix that nothing confirms while some signal exists (an unsure or different DGX vote,
    /// or evidence pointing elsewhere): confirm_marque. Bulk Create would still create it under the prefix.</item>
    /// <item>A real prefix with no signal at all (older lines): nothing to do.</item>
    /// </list>
    /// </summary>
    private static void AddMarqueIssue(PartsLineState l, Action<string, string> add)
    {
        var prefixMissing = PartsItemProvisioningService.IsUnresolvedPrefix(l.SkuPrefix);
        var resolvedTrue = string.Equals(l.MarqueResolved, "true", StringComparison.OrdinalIgnoreCase);
        var dgxPrefix = string.IsNullOrWhiteSpace(l.MarqueResolvedPrefix) ? null : l.MarqueResolvedPrefix.Trim().ToUpperInvariant();
        var evidence = EvidenceOf(l);
        var suggestion = SuggestMarque(l);

        if (prefixMissing || l.ReviewStatus == "needs_manufacturer")
        {
            if (!prefixMissing)
                add("assign_marque", $"Held for a marque: confirm {l.SkuPrefix!.Trim().ToUpperInvariant()} or pick another so it can be created.");
            else if (!l.HasItemData && l.ReviewStatus != "needs_manufacturer")
                return;   // not looked up yet: nothing to decide
            else if (resolvedTrue && dgxPrefix is not null)
                add("accept_marque", $"The DGX worked out the marque as {dgxPrefix} ({MarqueEvidence.Label(dgxPrefix)}): confirm it.");
            else
                add("assign_marque", "The manufacturer (marque) could not be worked out: pick it so an item code can be generated" +
                    (suggestion is null ? "." : $" (suggested: {suggestion}, {SuggestionSource(l, suggestion)})."));
            return;
        }

        var prefix = l.SkuPrefix!.Trim().ToUpperInvariant();
        var evidenceAgrees = evidence.Contains(prefix);
        var dgxAgrees = resolvedTrue && dgxPrefix == prefix;
        if (evidenceAgrees || dgxAgrees) return;
        if (l.MarqueResolved is null && evidence.Count == 0) return;   // no signal at all

        string why;
        if (evidence.Count > 0)
            why = $"the part's fit / OEM number points to {string.Join(" or ", evidence.Select(e => $"{e} ({MarqueEvidence.Label(e)})"))}";
        else if (resolvedTrue && dgxPrefix is not null)
            why = $"the DGX's marque check says {dgxPrefix}";
        else
            why = "nothing confirms it (no vehicle fit; the DGX's marque check was unsure" +
                  (string.IsNullOrWhiteSpace(l.MarqueTopCandidate) ? ")" : $", best guess {l.MarqueTopCandidate})");
        add("confirm_marque", $"Will be created under {prefix} ({MarqueEvidence.Label(prefix)}), but {why}. Keep {prefix}, or assign another.");
    }

    private static IReadOnlySet<string> EvidenceOf(PartsLineState l) =>
        MarqueEvidence.For(l.FitForAuto, (l.OemNumbers ?? Array.Empty<string>()).Concat(l.PartOems ?? Array.Empty<string>()));

    /// <summary>The marque to suggest: the line's own evidence when it names one, else the DGX's resolved marque, else its best guess.</summary>
    public static string? SuggestMarque(PartsLineState l)
    {
        var evidence = EvidenceOf(l);
        if (evidence.Count == 1) return evidence.First();
        if (string.Equals(l.MarqueResolved, "true", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(l.MarqueResolvedPrefix))
            return l.MarqueResolvedPrefix.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(l.MarqueTopCandidate) ? null : l.MarqueTopCandidate.Trim().ToUpperInvariant();
    }

    private static string SuggestionSource(PartsLineState l, string suggestion) =>
        EvidenceOf(l).Contains(suggestion) ? "from the part's fit / OEM number" : "the DGX's best guess";

    public static LineNextAction Classify(PartsLineState l, LinePriceState? price)
    {
        switch (l.ReviewStatus)
        {
            case "created": return new("created", Array.Empty<LineIssue>());
            case "matched": return new("matched", Array.Empty<LineIssue>());
            case "skip":    return new("skipped", Array.Empty<LineIssue>());
        }

        var issues = new List<LineIssue>();
        void Add(string code, string text) => issues.Add(new LineIssue(code, text));

        if (l.ReviewStatus == "create_failed")
            Add("create_failed", "Creating the SAP item failed: " + (string.IsNullOrWhiteSpace(l.CreateErrorMessage) ? "unknown error" : l.CreateErrorMessage));

        if (!l.IsPromotional)
        {
            if (l.UnitPriceForeign is not > 0m) Add("fix_data", "The line has no unit price.");
            if (l.Quantity is not > 0m) Add("fix_data", "The line has no quantity.");
        }

        if (l.ReviewStatus == "needs_confirmation")
            Add("confirm_match", string.IsNullOrWhiteSpace(l.SuggestedDonorItemCode)
                ? "Confirm whether this is an existing SAP item or a new one."
                : $"Possibly the existing SAP item {l.SuggestedDonorItemCode}" +
                  (string.IsNullOrWhiteSpace(l.SuggestedDonorSupplier) ? "" : $" ({l.SuggestedDonorSupplier})") +
                  ": use it, or create a new item.");

        // The DGX's flag is re-checked with the current name rules (PartNameMatch): many lines were flagged
        // only because the names differ in wording ("Lambda sensor; 4 wires" vs "Oxygen Sensor").
        var partCheck = l.NeedsPartReview && !l.Confirmed ? PartNameMatch.Verdict(l.Description, l.SelectedName) : PartNameMatch.Match;
        if (partCheck != PartNameMatch.Match)
            Add("check_part", (partCheck == PartNameMatch.Different
                    ? "The matched part looks like a different kind of part"
                    : "The matched part could not be confirmed as the same kind of part") +
                (string.IsNullOrWhiteSpace(l.SelectedName) ? "." : $": {l.SelectedName}" +
                    (string.IsNullOrWhiteSpace(l.SelectedSupplier) ? "." : $" ({l.SelectedSupplier}).")));

        if (l.ReviewStatus == "needs_manual")
            Add("manual", "No usable catalogue match: match it to an SAP item, choose a part, or skip it.");

        if (l.ReviewStatus == "pending" && l.EnrichmentSource is null && !l.IsPromotional)
            Add("awaiting_enrichment", "Waiting for the DGX to look the part up.");

        AddMarqueIssue(l, Add);

        if (l.ReviewStatus == "prefix_exhausted")
            Add("sku_range_full", $"No item codes left for prefix {l.SkuPrefix ?? "?"}: extend the SKU range, then create again.");

        if (l.ConfirmationRequired && !l.Confirmed && l.ReviewStatus is "pending" or "create_new" or "create_failed")
            Add("confirm_borrow", "Borrowed from another supplier's part" +
                (string.IsNullOrWhiteSpace(l.BorrowedFromArticle) ? "" : $" ({l.BorrowedFromArticle}" +
                    (string.IsNullOrWhiteSpace(l.BorrowedFromSupplier) ? ")" : $", {l.BorrowedFromSupplier})")) +
                ": confirm it before creating.");

        // Only a suggestion when nothing else is wrong: a line with problems is not "ready to queue".
        if (l.ReviewStatus == "pending" && l.EnrichmentSource is not null && issues.Count == 0)
            Add("queue", "Looked up and ready: mark it to create, or skip it.");

        if (l.ReviewStatus == "create_new" && price is not null && price.State != "approved")
            Add("price", price.State switch
            {
                "held"  => "Held in the price review.",
                "stale" => "Its approved price is out of date (the invoice price or exchange rate changed): review it again.",
                "error" => "Cannot be priced: " + (price.Error ?? "unknown error"),
                _       => "Price not reviewed yet.",
            });

        if (issues.Count == 0)
            return new("ready", issues);

        var ordered = issues.OrderBy(i => Rank[i.Code]).ToList();   // stable: same-code issues keep their order
        return new(ordered[0].Code, ordered);
    }
}

// ── Issues list ────────────────────────────────────────────────────────────────

/// <summary>A line as shown in the Issues list.</summary>
public sealed record IssueLine(
    Guid LineId, int LineNumber, string? ArticleNumber, string? Description, string? Brand,
    decimal? Quantity, decimal? UnitPriceForeign, string ReviewStatus, string NextAction,
    IReadOnlyList<LineIssue> Issues,
    string? MarqueSuggestion, string? SkuPrefix, int? ItemGroupCode,
    string? SuggestedItemCode, string? MatchedPartName, string? MatchedPartSupplier,
    string? PriceState, decimal? Pl03, bool Pl03Approved, IReadOnlyList<string> OemNumbers);

/// <summary>
/// Lines grouped by their next action. BulkActions are the action codes POST .../issues/bulk accepts for
/// this group ("open_prices" is a link to the price review, not a server action).
/// </summary>
public sealed record IssueGroup(string Code, string Title, string Help, IReadOnlyList<string> BulkActions, IReadOnlyList<IssueLine> Lines);

public sealed record IssuesSummary(
    int Total, int Done, int Created, int Matched, int Skipped, int Ready, int WithIssues,
    IReadOnlyDictionary<string, int> ByNextAction);

public sealed record InvoiceIssues(Guid DocumentId, string DocumentStatus, IssuesSummary Summary, IReadOnlyList<IssueGroup> Groups);

// ── Bulk actions ───────────────────────────────────────────────────────────────

public sealed record IssueBulkRequest(string Action, IReadOnlyList<Guid> LineIds, string? ManufacturerCode, int? ItemsGroupCode);

/// <summary>One line's outcome. ReviewStatus is the line's status after the action.</summary>
public sealed record IssueBulkLineResult(Guid LineId, bool Ok, string? Error, string? ReviewStatus);

/// <summary>JobId is set for retry_create, which runs as a background Bulk Create job.</summary>
public sealed record IssueBulkResult(string Action, int Succeeded, int Failed, IReadOnlyList<IssueBulkLineResult> Results, Guid? JobId, string? Note);

// ── Create & finish ────────────────────────────────────────────────────────────

/// <summary>A count of lines for one reason, with the Issues group that fixes it (null when none).</summary>
public sealed record FinishCount(string Code, string Title, int Count, string? IssuesGroup);

/// <summary>
/// What Bulk Create would do right now, and what still stops Complete Review. Lines queued to create
/// (create_new / create_failed) are split into WillCreate (by how their PL03 is set) and NotCreated (held
/// or not approved in the price review, or blocked by an issue Bulk Create would stop on). PartNotChecked
/// counts lines that will be created although their matched part was never confirmed. StillOpen counts
/// lines not queued and not finished, by next action; SkippableOpen is how many of them "skip all
/// undecided" would skip (pending + needs manual). CompleteBlockers lists the line statuses that stop
/// Complete Review.
/// </summary>
public sealed record FinishSummary(
    Guid DocumentId, string DocumentStatus, bool RequirePriceReview,
    int Total, int Created, int Matched, int Skipped,
    int WillCreate, IReadOnlyList<FinishCount> WillCreateBy,
    int NotCreated, IReadOnlyList<FinishCount> NotCreatedBy,
    int PartNotChecked,
    int StillOpen, IReadOnlyList<FinishCount> StillOpenBy, int SkippableOpen,
    bool CanComplete, IReadOnlyList<FinishCount> CompleteBlockers,
    Guid? RunningJobId, int MarqueNotConfirmed = 0);

/// <summary>
/// An invoice's progress for the invoice list. Done = created + matched + skipped; Ready = queued with
/// nothing wrong (prices not considered); Attention = everything else. TopIssue is the most common
/// open next action.
/// </summary>
public sealed record InvoiceProgress(int Total, int Done, int Ready, int Attention, FinishCount? TopIssue);

public interface IInvoiceIssuesService
{
    /// <summary>
    /// Marque decisions left on the invoice, by the Issues rules: ToConfirm = accept_marque + confirm_marque,
    /// ToPick = assign_marque. Shown in the invoice page's review bar.
    /// </summary>
    Task<(int ToConfirm, int ToPick)> GetMarqueCountsAsync(Guid documentId, CancellationToken ct);

    /// <summary>Progress of several invoices in one query (no price review: prices are not considered).</summary>
    Task<IReadOnlyDictionary<Guid, InvoiceProgress>> GetProgressAsync(IReadOnlyList<Guid> documentIds, CancellationToken ct);

    /// <summary>What Bulk Create would do now and what stops Complete Review. Null when the document does not exist.</summary>
    Task<FinishSummary?> GetFinishSummaryAsync(Guid documentId, CancellationToken ct);

    /// <summary>Every line's next action, with the open lines grouped by it. Null when the document does not exist.</summary>
    Task<InvoiceIssues?> GetAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// Apply one action to many lines. Each line is checked and done on its own: one failing line does not
    /// stop the others. Null when the document does not exist; throws ArgumentException for a bad request.
    /// </summary>
    Task<IssueBulkResult?> ApplyAsync(Guid documentId, IssueBulkRequest request, string requestedBy, CancellationToken ct);
}

/// <summary>
/// The Issues view of an Autohub invoice (Phase 1): what each line needs next, grouped, and bulk actions
/// that resolve many lines at once. The actions run the same steps as the per-line buttons.
/// </summary>
public sealed class InvoiceIssuesService : IInvoiceIssuesService
{
    public const int MaxBulkLines = 500;

    public static readonly IReadOnlyDictionary<string, (string Title, string Help, string[] Bulk)> Groups =
        new Dictionary<string, (string, string, string[])>
        {
            ["create_failed"] = ("Creation failed", "Creating the SAP item failed. Fix the cause, then retry.", new[] { "retry_create", "skip" }),
            ["fix_data"] = ("Missing price or quantity", "Correct the line (unit price / quantity) on the invoice, or skip it.", new[] { "skip" }),
            ["confirm_match"] = ("Confirm SAP match", "A matching SAP item may already exist. Use it, or create a new item.", new[] { "use_suggested_match", "confirm_and_queue", "skip" }),
            ["check_part"] = ("Check the matched part", "The DGX could not confirm the matched part is the same kind of part. Confirm it, or choose another part.", new[] { "confirm_and_queue", "skip" }),
            ["manual"] = ("Needs manual handling", "No usable catalogue match. Match it to an SAP item, choose a part, or skip it.", new[] { "queue", "skip" }),
            ["awaiting_enrichment"] = ("Waiting for lookup", "The DGX has not looked these parts up yet. They update automatically.", Array.Empty<string>()),
            ["assign_marque"] = ("Pick the marque", "The manufacturer could not be worked out. Pick it so an item code can be generated.", new[] { "assign_marque", "skip" }),
            ["accept_marque"] = ("Accept the DGX marque", "The DGX worked out the manufacturer. Accept it, or assign another.", new[] { "accept_marque", "assign_marque" }),
            ["confirm_marque"] = ("Confirm the marque", "These will be created under the marque shown, but nothing confirms it. Keep it, or assign another.", new[] { "keep_marque", "assign_marque" }),
            ["sku_range_full"] = ("SKU range full", "No item codes left for the prefix. Extend the SKU range, then create again.", Array.Empty<string>()),
            ["confirm_borrow"] = ("Confirm borrowed part", "Details are borrowed from another supplier's part. Confirm before creating.", new[] { "confirm_and_queue", "skip" }),
            ["queue"] = ("Ready to queue", "Looked up with no problems. Mark them to create, or skip them.", new[] { "queue", "skip" }),
            ["price"] = ("Price review", "Queued to create, but the price is not approved yet.", new[] { "open_prices" }),
        };

    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "skip", "queue", "confirm_and_queue", "use_suggested_match", "accept_marque", "keep_marque", "assign_marque", "retry_create",
    };

    private readonly IStagingPartsDocumentRepository _docs;
    private readonly IPartsLineStateRepository _lines;
    private readonly IPartsReviewRepository _review;
    private readonly IPriceReviewService _priceReview;
    private readonly IMarqueAssignmentService _marque;
    private readonly IAutohubSapB1Service _sap;
    private readonly AutohubBulkCreateJobService _jobs;
    private readonly AutohubPricingSettings _pricing;

    public InvoiceIssuesService(
        IStagingPartsDocumentRepository docs, IPartsLineStateRepository lines, IPartsReviewRepository review,
        IPriceReviewService priceReview, IMarqueAssignmentService marque, IAutohubSapB1Service sap,
        AutohubBulkCreateJobService jobs, IOptions<AutohubPricingSettings> pricing)
    {
        _docs = docs;
        _lines = lines;
        _review = review;
        _priceReview = priceReview;
        _marque = marque;
        _sap = sap;
        _jobs = jobs;
        _pricing = pricing.Value;
    }

    public async Task<InvoiceIssues?> GetAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return null;

        var lines = await _lines.ListAsync(documentId, ct);
        var prices = await PriceStatesAsync(documentId, ct);

        var classified = lines
            .Select(l => (Line: l, Next: LineIssueClassifier.Classify(l, prices.GetValueOrDefault(l.Id))))
            .ToList();

        var byAction = classified.GroupBy(c => c.Next.NextAction).ToDictionary(g => g.Key, g => g.Count());
        var groups = LineIssueClassifier.Order
            .Where(code => byAction.ContainsKey(code))
            .Select(code =>
            {
                var (title, help, bulk) = Groups[code];
                return new IssueGroup(code, title, help, bulk,
                    classified.Where(c => c.Next.NextAction == code)
                        .Select(c => ToIssueLine(c.Line, c.Next, prices.GetValueOrDefault(c.Line.Id))).ToList());
            })
            .ToList();

        int created = byAction.GetValueOrDefault("created"), matched = byAction.GetValueOrDefault("matched"),
            skipped = byAction.GetValueOrDefault("skipped"), ready = byAction.GetValueOrDefault("ready");
        var summary = new IssuesSummary(
            Total: lines.Count, Done: created + matched + skipped, Created: created, Matched: matched, Skipped: skipped,
            Ready: ready, WithIssues: lines.Count - created - matched - skipped - ready, ByNextAction: byAction);
        return new InvoiceIssues(documentId, doc.Status, summary, groups);
    }

    private static IssueLine ToIssueLine(PartsLineState l, LineNextAction next, LinePriceState? price) => new(
        LineId: l.Id, LineNumber: l.LineNumber, ArticleNumber: l.SupplierArticleNumber, Description: l.Description,
        Brand: l.Brand, Quantity: l.Quantity, UnitPriceForeign: l.UnitPriceForeign, ReviewStatus: l.ReviewStatus,
        NextAction: next.NextAction, Issues: next.Issues,
        MarqueSuggestion: LineIssueClassifier.SuggestMarque(l),
        SkuPrefix: l.SkuPrefix, ItemGroupCode: l.ItemGroupCode,
        SuggestedItemCode: l.SuggestedDonorItemCode, MatchedPartName: l.SelectedName, MatchedPartSupplier: l.SelectedSupplier,
        PriceState: price?.State, Pl03: price?.Pl03, Pl03Approved: price?.Pl03Approved ?? false,
        OemNumbers: l.OemNumbers ?? Array.Empty<string>());

    /// <summary>Price review state per line queued to create (the price review only covers those lines).</summary>
    private async Task<Dictionary<Guid, LinePriceState>> PriceStatesAsync(Guid documentId, CancellationToken ct)
    {
        var review = await _priceReview.GetReviewAsync(documentId, ct);
        var map = new Dictionary<Guid, LinePriceState>();
        foreach (var l in review?.Lines ?? Array.Empty<PriceReviewLine>())
        {
            var approvedPl03 = l.Decision is { Stale: false, Status: "approved", FinalPl03: { } f } ? f : (decimal?)null;
            var pl03 = approvedPl03 ?? l.FormulaPl03;
            map[l.LineId] = l.Decision switch
            {
                { Stale: true } => new LinePriceState("stale", null, pl03),
                { Status: "approved" } => new LinePriceState("approved", null, pl03, approvedPl03 is not null),
                { Status: "held" } => new LinePriceState("held", null, pl03),
                _ => l.Error is not null ? new LinePriceState("error", l.Error) : new LinePriceState("none", null, pl03),
            };
        }
        return map;
    }

    public async Task<(int ToConfirm, int ToPick)> GetMarqueCountsAsync(Guid documentId, CancellationToken ct)
    {
        int confirm = 0, pick = 0;
        foreach (var l in await _lines.ListAsync(documentId, ct))
        {
            var issues = LineIssueClassifier.Classify(l, null).Issues;
            if (issues.Any(i => i.Code == "assign_marque")) pick++;
            else if (issues.Any(i => i.Code is "accept_marque" or "confirm_marque")) confirm++;
        }
        return (confirm, pick);
    }

    // ── Invoice list progress ───────────────────────────────────────────────────

    public async Task<IReadOnlyDictionary<Guid, InvoiceProgress>> GetProgressAsync(
        IReadOnlyList<Guid> documentIds, CancellationToken ct)
    {
        var byDoc = await _lines.ListForDocumentsAsync(documentIds, ct);
        var result = new Dictionary<Guid, InvoiceProgress>();
        foreach (var (docId, lines) in byDoc)
        {
            var next = lines.Select(l => LineIssueClassifier.Classify(l, null).NextAction).ToList();
            int done = next.Count(n => n is "created" or "matched" or "skipped"), ready = next.Count(n => n == "ready");
            var top = next.Where(n => Groups.ContainsKey(n)).GroupBy(n => n)
                .OrderByDescending(g => g.Count()).ThenBy(g => LineIssueClassifier.Order.ToList().IndexOf(g.Key))
                .Select(g => new FinishCount(g.Key, Groups[g.Key].Title, g.Count(), g.Key))
                .FirstOrDefault();
            result[docId] = new InvoiceProgress(lines.Count, done, ready, lines.Count - done - ready, top);
        }
        return result;
    }

    // ── Create & finish ─────────────────────────────────────────────────────────

    /// <summary>Issues Bulk Create stops on for a queued line (it holds, parks or fails it instead of creating).</summary>
    private static readonly string[] CreateBlockers = { "fix_data", "assign_marque", "accept_marque", "sku_range_full", "confirm_borrow" };

    /// <summary>Line statuses that stop Complete Review (AutohubDocumentsController.CompleteReview).</summary>
    private static readonly (string Status, string Title)[] CompleteBlocking =
    {
        ("pending", "Pending"), ("needs_manual", "Needs manual handling"), ("needs_confirmation", "Confirm SAP match"),
        ("needs_manufacturer", "Waiting for a marque"), ("prefix_exhausted", "SKU range full"),
        ("create_new", "Queued, not created yet"), ("create_failed", "Creation failed"),
    };

    public async Task<FinishSummary?> GetFinishSummaryAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return null;

        var lines = await _lines.ListAsync(documentId, ct);
        var review = await _priceReview.GetReviewAsync(documentId, ct);
        var reviewed = review?.Lines.ToDictionary(r => r.LineId) ?? new Dictionary<Guid, PriceReviewLine>();
        var required = _pricing.RequirePriceReview;

        int approvedCustom = 0, approvedCalculated = 0, unreviewed = 0, held = 0, notApproved = 0, partNotChecked = 0, marqueNotConfirmed = 0;
        var blocked = new Dictionary<string, int>();
        var open = new Dictionary<string, int>();
        foreach (var l in lines)
        {
            var next = LineIssueClassifier.Classify(l, null);
            if (next.IsDone) continue;
            if (l.ReviewStatus is not ("create_new" or "create_failed"))
            {
                open[next.NextAction] = open.GetValueOrDefault(next.NextAction) + 1;
                continue;
            }
            var blocker = next.Issues.FirstOrDefault(i => CreateBlockers.Contains(i.Code));
            if (blocker is not null)
            {
                blocked[blocker.Code] = blocked.GetValueOrDefault(blocker.Code) + 1;
                continue;
            }
            var rl = reviewed.GetValueOrDefault(l.Id);
            var (gate, priceOverride) = PartsItemCreationService.ApplyPriceReview(rl, required);
            if (gate == PartsItemCreationService.PriceReviewAction.Held) { held++; continue; }
            if (gate == PartsItemCreationService.PriceReviewAction.NotApproved) { notApproved++; continue; }
            if (priceOverride is not null) approvedCustom++;
            else if (rl?.Decision is { Stale: false, Status: "approved" }) approvedCalculated++;
            else unreviewed++;
            if (next.Issues.Any(i => i.Code == "check_part")) partNotChecked++;
            if (next.Issues.Any(i => i.Code == "confirm_marque")) marqueNotConfirmed++;
        }

        FinishCount Group(string code, int n) => new(code, Groups.TryGetValue(code, out var g) ? g.Title : code, n,
            Groups.ContainsKey(code) ? code : null);

        var willBy = new List<FinishCount>
        {
            new("approved_custom", "Approved at a PL03 you set", approvedCustom, null),
            new("approved_calculated", "Approved at the calculated PL03", approvedCalculated, null),
            new("unreviewed", "Not price-reviewed: calculated PL03", unreviewed, "price"),
        };
        var notBy = new List<FinishCount>
        {
            new("price_held", "Held in the price review", held, "price"),
            new("not_price_approved", "Price not approved (review is required)", notApproved, "price"),
        };
        notBy.AddRange(CreateBlockers.Where(blocked.ContainsKey).Select(c => Group(c, blocked[c])));
        var openBy = LineIssueClassifier.Order.Where(open.ContainsKey).Select(c => Group(c, open[c])).ToList();

        var completeBlockers = CompleteBlocking
            .Select(b => new FinishCount(b.Status, b.Title, lines.Count(l => l.ReviewStatus == b.Status), null))
            .Where(b => b.Count > 0)
            .ToList();

        return new FinishSummary(
            DocumentId: documentId, DocumentStatus: doc.Status, RequirePriceReview: required,
            Total: lines.Count,
            Created: lines.Count(l => l.ReviewStatus == "created"),
            Matched: lines.Count(l => l.ReviewStatus == "matched"),
            Skipped: lines.Count(l => l.ReviewStatus == "skip"),
            WillCreate: approvedCustom + approvedCalculated + unreviewed,
            WillCreateBy: willBy.Where(x => x.Count > 0).ToList(),
            NotCreated: held + notApproved + blocked.Values.Sum(),
            NotCreatedBy: notBy.Where(x => x.Count > 0).ToList(),
            PartNotChecked: partNotChecked,
            StillOpen: open.Values.Sum(), StillOpenBy: openBy,
            SkippableOpen: lines.Count(l => l.ReviewStatus is "pending" or "needs_manual"),
            CanComplete: doc.Status == "extracted" && completeBlockers.Count == 0,
            CompleteBlockers: completeBlockers,
            RunningJobId: _jobs.GetRunningForDocument(documentId)?.JobId,
            MarqueNotConfirmed: marqueNotConfirmed);
    }

    // ── Bulk ───────────────────────────────────────────────────────────────────

    public async Task<IssueBulkResult?> ApplyAsync(
        Guid documentId, IssueBulkRequest request, string requestedBy, CancellationToken ct)
    {
        var action = (request.Action ?? "").Trim().ToLowerInvariant();
        if (!Actions.Contains(action))
            throw new ArgumentException($"Unknown action '{request.Action}'. Use one of: {string.Join(", ", Actions)}.");
        var ids = (request.LineIds ?? Array.Empty<Guid>()).Where(i => i != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) throw new ArgumentException("line_ids is required.");
        if (ids.Count > MaxBulkLines) throw new ArgumentException($"At most {MaxBulkLines} lines per request.");
        if (action == "assign_marque" && string.IsNullOrWhiteSpace(request.ManufacturerCode))
            throw new ArgumentException("manufacturer_code is required for assign_marque.");

        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return null;
        if (doc.Status != "extracted")
            throw new ArgumentException($"The invoice is '{doc.Status}': lines can only be changed while it is being reviewed.");

        var lines = (await _lines.ListAsync(documentId, ct)).ToDictionary(l => l.Id);
        var results = new List<IssueBulkLineResult>(ids.Count);
        IssueBulkLineResult Fail(Guid id, string error) => new(id, false, error, lines.GetValueOrDefault(id)?.ReviewStatus);

        // Lines not on this document fail individually.
        var present = new List<PartsLineState>();
        foreach (var id in ids)
        {
            if (lines.TryGetValue(id, out var l)) present.Add(l);
            else results.Add(Fail(id, "Line not found on this document."));
        }

        Guid? jobId = null;
        string? note = null;
        switch (action)
        {
            case "skip":
                foreach (var l in present)
                {
                    if (l.ReviewStatus is "created" or "matched") { results.Add(Fail(l.Id, $"Line is already {l.ReviewStatus}.")); continue; }
                    await _review.SetReviewStatusAsync(l.Id, "skip", null, ct);
                    results.Add(new(l.Id, true, null, "skip"));
                }
                break;

            case "queue":
                foreach (var l in present)
                {
                    if (l.ReviewStatus is not ("pending" or "needs_manual" or "create_failed" or "create_new"))
                    { results.Add(Fail(l.Id, $"A '{l.ReviewStatus}' line cannot be marked to create this way.")); continue; }
                    if (l.ReviewStatus == "pending" && l.EnrichmentSource is null && !l.IsPromotional)
                    { results.Add(Fail(l.Id, "The part has not been looked up yet.")); continue; }
                    await _review.SetReviewStatusAsync(l.Id, "create_new", null, ct);
                    results.Add(new(l.Id, true, null, "create_new"));
                }
                break;

            case "confirm_and_queue":
                // Same as the per-line "confirm" (create-new with confirmed=true): sign off the matched or
                // borrowed part and queue the line. On a confirm-match line this means "create a new item".
                foreach (var l in present)
                {
                    if (l.ReviewStatus is "created" or "matched" or "skip")
                    { results.Add(Fail(l.Id, $"Line is {l.ReviewStatus}.")); continue; }
                    if (l.EnrichmentSource is null)
                    { results.Add(Fail(l.Id, "The part has not been looked up yet, so there is nothing to confirm.")); continue; }
                    await _review.SetReviewStatusAsync(l.Id, "create_new", null, ct);
                    await _review.ConfirmEnrichmentAsync(l.Id, requestedBy, ct);
                    results.Add(new(l.Id, true, null, "create_new"));
                }
                break;

            case "use_suggested_match":
                foreach (var l in present)
                {
                    if (l.ReviewStatus != "needs_confirmation" || string.IsNullOrWhiteSpace(l.SuggestedDonorItemCode))
                    { results.Add(Fail(l.Id, "The line has no suggested SAP item to use.")); continue; }
                    var code = l.SuggestedDonorItemCode.Trim();
                    bool exists;
                    try { exists = await _sap.ItemExistsAsync(code); }
                    catch (Exception ex) { results.Add(Fail(l.Id, $"Could not check {code} in SAP: {ex.Message}")); continue; }
                    if (!exists) { results.Add(Fail(l.Id, $"{code} does not exist in the Autohub SAP company.")); continue; }
                    await _review.SetReviewStatusAsync(l.Id, "matched", code, ct);
                    results.Add(new(l.Id, true, null, "matched"));
                }
                break;

            case "accept_marque":
            {
                var updated = (await _lines.ApplyResolvedMarquesAsync(documentId, present.Select(l => l.Id).ToList(), ct)).ToHashSet();
                foreach (var l in present)
                    results.Add(updated.Contains(l.Id)
                        ? new(l.Id, true, null, "create_new")
                        : Fail(l.Id, "No DGX-resolved marque to accept on this line."));
                break;
            }

            case "keep_marque":
            {
                var kept = (await _lines.KeepCurrentMarqueAsync(documentId, present.Select(l => l.Id).ToList(), requestedBy, ct)).ToHashSet();
                foreach (var l in present)
                    results.Add(kept.Contains(l.Id)
                        ? new(l.Id, true, null, l.ReviewStatus)
                        : Fail(l.Id, "The line has no marque to keep (assign one instead)."));
                break;
            }

            case "assign_marque":
                foreach (var l in present)
                {
                    var r = await _marque.AssignAsync(documentId, l.Id, request.ManufacturerCode, request.ItemsGroupCode, requestedBy, ct);
                    results.Add(r.Ok ? new(l.Id, true, null, r.Line?.ReviewStatus) : Fail(l.Id, r.Error ?? "Failed."));
                }
                break;

            case "retry_create":
            {
                // Retry through Bulk Create with an explicit line list, carrying each line's price review
                // decision (an explicit list does not read the review itself).
                if (_jobs.GetRunningForDocument(documentId) is { } running)
                {
                    foreach (var l in present)
                        results.Add(Fail(l.Id, "A Bulk Create is already running for this invoice. Retry when it finishes."));
                    jobId = running.JobId;
                    note = "A Bulk Create is already running for this invoice; nothing was retried.";
                    break;
                }
                var review = await _priceReview.GetReviewAsync(documentId, ct);
                var reviewed = review?.Lines.ToDictionary(r => r.LineId) ?? new Dictionary<Guid, PriceReviewLine>();
                var selections = new List<BulkCreateLineSelection>();
                foreach (var l in present)
                {
                    if (l.ReviewStatus != "create_failed")
                    { results.Add(Fail(l.Id, "Only lines whose creation failed can be retried here.")); continue; }
                    var (gate, priceOverride) = PartsItemCreationService.ApplyPriceReview(
                        reviewed.GetValueOrDefault(l.Id), _pricing.RequirePriceReview);
                    if (gate == PartsItemCreationService.PriceReviewAction.Held)
                    { results.Add(Fail(l.Id, "Held in the price review.")); continue; }
                    if (gate == PartsItemCreationService.PriceReviewAction.NotApproved)
                    { results.Add(Fail(l.Id, "Price not approved (price review is required).")); continue; }
                    selections.Add(new BulkCreateLineSelection(l.Id, priceOverride));
                    results.Add(new(l.Id, true, null, "create_failed"));
                }
                if (selections.Count > 0)
                {
                    jobId = _jobs.Start(documentId, selections).JobId;
                    note = $"Retrying {selections.Count} line(s) in the background. Follow the job for the result.";
                }
                break;
            }
        }

        // Keep results in request order.
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        results.Sort((a, b) => order[a.LineId].CompareTo(order[b.LineId]));
        return new IssueBulkResult(action, results.Count(r => r.Ok), results.Count(r => !r.Ok), results, jobId, note);
    }
}
