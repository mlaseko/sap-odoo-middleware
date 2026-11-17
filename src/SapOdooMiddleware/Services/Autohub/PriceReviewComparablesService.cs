using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// A related part: catalogue details from Neon (name, part, brand, supplier, image, vehicle count, stock
/// from the SAP mirror) and from SAP its prices (PL03, PL05, average cost) and its article number. When
/// the catalogue row carrying the item code has another article number than SAP, SAP's article and name
/// are shown and CatalogueArticleNumber holds the catalogue's, so the wrong catalogue link can be fixed.
/// SameArticle: the item's SAP article number is the invoice line's own, so the part may already be in SAP.
/// </summary>
public sealed record ComparablePart(
    string ItemCode, string? Name, string? ItemName, string? PartComponent, string? PartGroup,
    string? Brand, string Tier, bool TierMapped, string? ArticleNumber, string? SupplierName,
    string? ImageUrl, int VehiclesCount, decimal OnHand, decimal Available,
    decimal? Pl03, decimal? Pl05, decimal? AvgCost,
    IReadOnlyList<string> SharedOems, IReadOnlyList<string> Via,
    string? CatalogueArticleNumber = null, bool SameArticle = false);

/// <summary>PL03 spread of a group of related parts (only parts with a PL03 count).</summary>
public sealed record ComparableStats(int N, decimal Min, decimal Median, decimal Max);

/// <summary>
/// Related parts for one price review line, split so the reviewer compares like with like:
/// Same = same part name and same quality tier; OtherTier = same part name, other tiers (for reference);
/// parts with a different part name are only counted. When the line's part name is unknown, every
/// related part is split by tier alone (PartNameKnown = false).
///
/// LinePl03 is the price the line would be created at (the reviewer's current decision, else the
/// calculated PL03). VsMedian = LinePl03 ÷ the Same median − 1 (0.38 = 38% above), and Position is
/// "above", "below" or "in_line" against the PriceReviewComparableBand setting (returned as
/// ComparableBand so a screen can re-place a PL03 the reviewer is typing). Flags carry the
/// cost check: invoice cost far above the SAP average cost of the same part.
/// </summary>
public sealed record PriceReviewComparables(
    Guid LineId, string? Brand, string Tier, bool TierMapped, string? PartName, bool PartNameKnown,
    decimal? LinePl03, string? LinePl03Source, decimal? CifTzs,
    IReadOnlyList<ComparablePart> Same, IReadOnlyList<ComparablePart> OtherTier, int OtherPartCount,
    ComparableStats? SameStats, ComparableStats? OtherTierStats,
    decimal? VsMedian, string? Position, decimal ComparableBand, decimal? MedianAvgCost, int MedianAvgCostN,
    IReadOnlyList<PriceReviewFlag> Flags, string? Note);

/// <summary>A line whose related parts could not be read (the others are still returned).</summary>
public sealed record PriceReviewComparablesError(Guid LineId, string Error);

/// <summary>Related parts for every line of the price review, read in one go when the page opens.</summary>
public sealed record PriceReviewComparablesBatch(
    IReadOnlyList<PriceReviewComparables> Lines, IReadOnlyList<PriceReviewComparablesError> Errors);

public interface IPriceReviewComparablesService
{
    /// <summary>
    /// Related parts and their SAP prices for one line in the price review. Null when the line is not
    /// on the document. Read-only.
    /// </summary>
    Task<PriceReviewComparables?> GetAsync(Guid documentId, Guid lineId, CancellationToken ct);

    /// <summary>
    /// Related parts for every line in the price review, with the review priced once, the brand tiers
    /// read once and the SAP prices of all related items read in one query. Null when the document does
    /// not exist. A line that fails is listed in Errors; the others are still returned. Read-only.
    /// </summary>
    Task<PriceReviewComparablesBatch?> GetForDocumentAsync(Guid documentId, CancellationToken ct);
}

/// <summary>
/// Price review step 3, ported from the DGX price gate (price_gate/gate.py + comparables.py). Related
/// parts are found in parts_catalog (shared genuine OEM numbers, oitm_related_parts) and described from
/// Neon, which holds richer details than SAP. SAP is read only for the prices of the item codes found
/// (PL03, PL05, average cost), because Neon's oitm.sell_price_tzs is not a reliable selling price.
/// Comparison is on PL03 only: PL01 means raw CIF on older items and CIF × 1.25 on newer ones.
/// </summary>
public sealed class PriceReviewComparablesService : IPriceReviewComparablesService
{
    /// <summary>At most this many related parts per line (most shared OEMs first).</summary>
    public const int CandidateLimit = 60;

    /// <summary>A median needs at least this many same-part items with a cost.</summary>
    public const int CostMedianMinN = 3;

    /// <summary>Invoice cost above this multiple of the same-part median cost is flagged.</summary>
    public const decimal CostOutlierX = 3.0m;

    private readonly IPriceReviewService _review;
    private readonly IPartsReviewRepository _lines;
    private readonly IPartsComparablesRepository _comparables;
    private readonly IAutohubInventorySqlService _sap;
    private readonly AutohubPricingSettings _settings;

    public PriceReviewComparablesService(
        IPriceReviewService review, IPartsReviewRepository lines, IPartsComparablesRepository comparables,
        IAutohubInventorySqlService sap, IOptions<AutohubPricingSettings> settings)
    {
        _review = review;
        _lines = lines;
        _comparables = comparables;
        _sap = sap;
        _settings = settings.Value;
    }

    /// <summary>How many lines read their related parts from Neon at the same time in the batch.</summary>
    public const int BatchConcurrency = 4;

    public async Task<PriceReviewComparables?> GetAsync(Guid documentId, Guid lineId, CancellationToken ct)
    {
        var line = await _lines.GetByIdAsync(lineId, ct);
        if (line is null || line.DocumentId != documentId) return null;
        var anchors = await _comparables.GetLineAnchorsAsync(documentId, lineId, ct);
        if (anchors is null) return null;

        // The price the line would be created at, when it is in the review.
        var review = await _review.GetReviewAsync(documentId, ct);
        var reviewLine = review?.Lines.FirstOrDefault(l => l.LineId == lineId);
        var tiers = await _comparables.GetBrandTiersAsync(ct);

        var candidates = await _comparables.FindCandidatesAsync(
            anchors.AnchorIds, SplitOems(line.OemNumbers), anchors.OwnItemCodes, CandidateLimit, ct);
        var sap = await _sap.GetComparableItemsAsync(candidates.Select(c => c.ItemCode).ToList(), ct);
        return Build(line, anchors, reviewLine, tiers, candidates, sap);
    }

    public async Task<PriceReviewComparablesBatch?> GetForDocumentAsync(Guid documentId, CancellationToken ct)
    {
        var review = await _review.GetReviewAsync(documentId, ct);
        if (review is null) return null;
        var tiers = await _comparables.GetBrandTiersAsync(ct);
        var reviewLines = review.Lines.ToDictionary(l => l.LineId);
        var lines = (await _lines.ListByDocumentAsync(documentId, ct))
            .Where(l => reviewLines.ContainsKey(l.Id))
            .OrderBy(l => l.LineNumber)
            .ToList();

        // Neon lookups per line, a few at a time; each opens its own connection.
        var found = new (PartsLineAnchors? Anchors, IReadOnlyList<PartsComparableCandidate> Candidates, string? Error)[lines.Count];
        using var gate = new SemaphoreSlim(BatchConcurrency);
        await Task.WhenAll(lines.Select(async (line, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var anchors = await _comparables.GetLineAnchorsAsync(documentId, line.Id, ct);
                var candidates = anchors is null
                    ? Array.Empty<PartsComparableCandidate>()
                    : await _comparables.FindCandidatesAsync(
                        anchors.AnchorIds, SplitOems(line.OemNumbers), anchors.OwnItemCodes, CandidateLimit, ct);
                found[i] = (anchors, candidates, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                found[i] = (null, Array.Empty<PartsComparableCandidate>(), "Could not read related parts: " + ex.Message);
            }
            finally
            {
                gate.Release();
            }
        }));

        // One SAP query for the prices of every related item of every line.
        var sap = await _sap.GetComparableItemsAsync(
            found.SelectMany(f => f.Candidates).Select(c => c.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);

        var results = new List<PriceReviewComparables>(lines.Count);
        var errors = new List<PriceReviewComparablesError>();
        for (int i = 0; i < lines.Count; i++)
        {
            var (anchors, candidates, error) = found[i];
            if (error is not null) { errors.Add(new(lines[i].Id, error)); continue; }
            if (anchors is null) continue;
            results.Add(Build(lines[i], anchors, reviewLines[lines[i].Id], tiers, candidates, sap));
        }
        return new PriceReviewComparablesBatch(results, errors);
    }

    /// <summary>The comparison for one line from what was read for it (shared by the single and batch reads).</summary>
    private PriceReviewComparables Build(
        PartsReviewLineRow line, PartsLineAnchors anchors, PriceReviewLine? reviewLine,
        IReadOnlyDictionary<string, string> tiers, IReadOnlyList<PartsComparableCandidate> candidates,
        IReadOnlyDictionary<string, ComparableSapItem> sap)
    {
        var lineId = line.Id;
        decimal? linePl03 = null;
        string? source = null;
        if (reviewLine?.Decision is { Stale: false, FinalPl03: { } final })
            (linePl03, source) = (final, "decision");
        else if (reviewLine?.FormulaPl03 is { } formula)
            (linePl03, source) = (formula, "calculated");

        var (tier, mapped) = TierOf(line.Brand, tiers);
        var tokens = SplitOems(line.OemNumbers);

        var partKey = NormPart(anchors.PartComponent);
        var lineArticle = NormArticle(line.SupplierArticleNumber);
        var sameArticleItems = new List<string>();
        var same = new List<ComparablePart>();
        var otherTier = new List<ComparablePart>();
        var samePartCosts = new List<decimal>();
        int otherPart = 0;
        foreach (var c in candidates)
        {
            // Details from Neon; SAP only prices it. An item SAP does not have, or holds as inactive, is dropped.
            if (!sap.TryGetValue(c.ItemCode, out var price) || !price.Active) continue;

            var (cTier, cMapped) = TierOf(c.Brand, tiers);
            // SAP says what the item is: a catalogue row with another article number describes another part.
            var sapArticle = price.ArticleNumber;
            bool catalogueDisagrees = sapArticle is not null && c.ArticleNumber is not null
                && NormArticle(sapArticle) != NormArticle(c.ArticleNumber);
            var article = sapArticle ?? c.ArticleNumber;
            bool sameArticle = lineArticle is not null && article is not null && NormArticle(article) == lineArticle;
            var part = new ComparablePart(
                ItemCode: c.ItemCode,
                Name: catalogueDisagrees ? (c.ItemName ?? price.ItemName ?? c.Name) : (c.Name ?? c.ItemName),
                ItemName: c.ItemName,
                PartComponent: c.PartComponent, PartGroup: c.PartGroup,
                Brand: c.Brand, Tier: cTier, TierMapped: cMapped,
                ArticleNumber: article, SupplierName: c.SupplierName, ImageUrl: c.ImageUrl,
                VehiclesCount: c.VehiclesCount, OnHand: c.OnHand, Available: c.Available,
                Pl03: price.Pl03, Pl05: price.Pl05, AvgCost: price.AvgCost,
                SharedOems: c.SharedOems, Via: c.Via,
                CatalogueArticleNumber: catalogueDisagrees ? c.ArticleNumber : null,
                SameArticle: sameArticle);
            if (sameArticle) sameArticleItems.Add(c.ItemCode);

            if (partKey is not null && NormPart(c.PartComponent) != partKey)
            {
                otherPart++;
                continue;
            }
            if (price.AvgCost is { } cost) samePartCosts.Add(cost);
            (cTier == tier ? same : otherTier).Add(part);
        }

        var sameStats = Stats(same.Select(p => p.Pl03));
        decimal? vsMedian = linePl03 is { } p03 && sameStats is { Median: > 0m } s
            ? Math.Round(p03 / s.Median - 1m, 4, MidpointRounding.AwayFromZero)
            : null;
        string? position = vsMedian switch
        {
            null => null,
            var v when v > _settings.PriceReviewComparableBand => "above",
            var v when v < -_settings.PriceReviewComparableBand => "below",
            _ => "in_line",
        };

        // Cost check, as the DGX gate did: invoice cost against the SAP average cost of the same part.
        var flags = new List<PriceReviewFlag>();
        decimal? medianCost = samePartCosts.Count >= CostMedianMinN ? Median(samePartCosts) : null;
        if (partKey is not null && medianCost is { } mc && reviewLine?.CifTzs is { } cif && cif > CostOutlierX * mc)
            flags.Add(new("cost_outlier",
                $"Invoice cost is {cif / mc:0.0}x the median SAP average cost ({mc:N0}) of {samePartCosts.Count} items of the same part."));
        if (position == "above")
            flags.Add(new("above_related", $"PL03 is {vsMedian:P0} above the median of {sameStats!.N} related parts."));
        else if (position == "below")
            flags.Add(new("below_related", $"PL03 is {-vsMedian:P0} below the median of {sameStats!.N} related parts."));

        if (sameArticleItems.Count > 0)
            flags.Add(new("same_article",
                $"{string.Join(", ", sameArticleItems)} in SAP has the same supplier article ({line.SupplierArticleNumber?.Trim()}): " +
                "this part may already exist in SAP; consider matching the line instead of creating it."));

        string? note = null;
        if (anchors.AnchorIds.Count == 0 && tokens.Count == 0)
            note = "The line has no linked catalogue part and no OEM number, so related parts cannot be found.";
        else if (same.Count == 0 && otherTier.Count == 0)
            note = "No related parts found in SAP for this line.";

        return new PriceReviewComparables(
            LineId: lineId, Brand: line.Brand, Tier: tier, TierMapped: mapped,
            PartName: anchors.PartComponent, PartNameKnown: partKey is not null,
            LinePl03: linePl03, LinePl03Source: source, CifTzs: reviewLine?.CifTzs,
            Same: same, OtherTier: otherTier, OtherPartCount: otherPart,
            SameStats: sameStats, OtherTierStats: Stats(otherTier.Select(p => p.Pl03)),
            VsMedian: vsMedian, Position: position, ComparableBand: _settings.PriceReviewComparableBand,
            MedianAvgCost: medianCost, MedianAvgCostN: samePartCosts.Count,
            Flags: flags, Note: note);
    }

    // ── Helpers (same rules as the DGX gate) ───────────────────────────────────

    /// <summary>Invoice OEM cells can hold several numbers ("LR110708 LR079950"). Normalised, ≥5 chars, with a digit.</summary>
    internal static IReadOnlyList<string> SplitOems(IEnumerable<string>? values)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var v in values ?? Enumerable.Empty<string>())
            foreach (var raw in Regex.Split(v ?? "", @"[\s,;/]+"))
            {
                var t = Regex.Replace(raw, @"[\s./\-]", "").ToUpperInvariant();
                if (t.Length >= 5 && t.Any(char.IsDigit)) set.Add(t);
            }
        return set.ToList();
    }

    /// <summary>Article number for comparison: letters and digits only, upper case. Null when empty.</summary>
    internal static string? NormArticle(string? article) => ArticleNumbers.Normalize(article);

    /// <summary>Lower-case, letters/digits/spaces only, single spaces. Null when empty.</summary>
    internal static string? NormPart(string? name)
    {
        var s = Regex.Replace(Regex.Replace((name ?? "").ToLowerInvariant(), "[^a-z0-9 ]", " "), @"\s+", " ").Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// Tier for a selling brand from parts_brand_tier: the whole brand, else its first word ("VIKA DPA"),
    /// else economy and not mapped.
    /// </summary>
    internal static (string Tier, bool Mapped) TierOf(string? brand, IReadOnlyDictionary<string, string> tiers)
    {
        var key = Regex.Replace((brand ?? "").Trim().ToUpperInvariant(), @"\s+", " ");
        if (key.Length > 0 && tiers.TryGetValue(key, out var t)) return (t, true);
        var first = key.Split(' ')[0];
        if (first.Length > 0 && tiers.TryGetValue(first, out var f)) return (f, true);
        return ("economy", false);
    }

    internal static ComparableStats? Stats(IEnumerable<decimal?> values)
    {
        var v = values.Where(x => x is > 0m).Select(x => x!.Value).OrderBy(x => x).ToList();
        return v.Count == 0 ? null : new ComparableStats(v.Count, v[0], Median(v), v[^1]);
    }

    private static decimal Median(List<decimal> values)
    {
        var v = values.OrderBy(x => x).ToList();
        int n = v.Count;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2m;
    }
}
