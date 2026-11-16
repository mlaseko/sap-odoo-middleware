using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>A related part with its SAP prices. Name is the SAP part name when set, else the item name.</summary>
public sealed record ComparablePart(
    string ItemCode, string? Name, string? Brand, string Tier, bool TierMapped, string? PartComponent,
    decimal? Pl03, decimal? Pl05, decimal? AvgCost, decimal OnHand,
    IReadOnlyList<string> SharedOems, IReadOnlyList<string> Via);

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
/// "above", "below" or "in_line" against the PriceReviewComparableBand setting. Flags carry the
/// cost check: invoice cost far above the SAP average cost of the same part.
/// </summary>
public sealed record PriceReviewComparables(
    Guid LineId, string? Brand, string Tier, bool TierMapped, string? PartName, bool PartNameKnown,
    decimal? LinePl03, string? LinePl03Source, decimal? CifTzs,
    IReadOnlyList<ComparablePart> Same, IReadOnlyList<ComparablePart> OtherTier, int OtherPartCount,
    ComparableStats? SameStats, ComparableStats? OtherTierStats,
    decimal? VsMedian, string? Position, decimal? MedianAvgCost, int MedianAvgCostN,
    IReadOnlyList<PriceReviewFlag> Flags, string? Note);

public interface IPriceReviewComparablesService
{
    /// <summary>
    /// Related parts and their SAP prices for one line in the price review. Null when the line is not
    /// on the document. Read-only.
    /// </summary>
    Task<PriceReviewComparables?> GetAsync(Guid documentId, Guid lineId, CancellationToken ct);
}

/// <summary>
/// Price review step 3, ported from the DGX price gate (price_gate/gate.py + comparables.py). Related
/// parts come from parts_catalog (shared genuine OEM numbers, oitm_related_parts); prices, stock and
/// average cost come from SAP, because Neon's oitm.sell_price_tzs is not a reliable selling price.
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

    public async Task<PriceReviewComparables?> GetAsync(Guid documentId, Guid lineId, CancellationToken ct)
    {
        var line = await _lines.GetByIdAsync(lineId, ct);
        if (line is null || line.DocumentId != documentId) return null;
        var anchors = await _comparables.GetLineAnchorsAsync(documentId, lineId, ct);
        if (anchors is null) return null;

        // The price the line would be created at, when it is in the review.
        var review = await _review.GetReviewAsync(documentId, ct);
        var reviewLine = review?.Lines.FirstOrDefault(l => l.LineId == lineId);
        decimal? linePl03 = null;
        string? source = null;
        if (reviewLine?.Decision is { Stale: false, FinalPl03: { } final })
            (linePl03, source) = (final, "decision");
        else if (reviewLine?.FormulaPl03 is { } formula)
            (linePl03, source) = (formula, "calculated");

        var tiers = await _comparables.GetBrandTiersAsync(ct);
        var (tier, mapped) = TierOf(line.Brand, tiers);
        var tokens = SplitOems(line.OemNumbers);

        var candidates = await _comparables.FindCandidatesAsync(
            anchors.AnchorIds, tokens, anchors.OwnItemCodes, CandidateLimit, ct);
        var sap = await _sap.GetComparableItemsAsync(candidates.Select(c => c.ItemCode).ToList(), ct);

        var partKey = NormPart(anchors.PartComponent);
        var same = new List<ComparablePart>();
        var otherTier = new List<ComparablePart>();
        var samePartCosts = new List<decimal>();
        int otherPart = 0;
        foreach (var c in candidates)
        {
            if (!sap.TryGetValue(c.ItemCode, out var item) || !item.Active) continue;   // only sold, active items

            var brand = item.Brand ?? c.Brand;
            var (cTier, cMapped) = TierOf(brand, tiers);
            var part = new ComparablePart(
                ItemCode: item.ItemCode, Name: item.PartName ?? item.ItemName ?? c.Name, Brand: brand,
                Tier: cTier, TierMapped: cMapped, PartComponent: c.PartComponent,
                Pl03: item.Pl03, Pl05: item.Pl05, AvgCost: item.AvgCost, OnHand: item.OnHand,
                SharedOems: c.SharedOems, Via: c.Via);

            if (partKey is not null && NormPart(c.PartComponent) != partKey)
            {
                otherPart++;
                continue;
            }
            if (item.AvgCost is { } cost) samePartCosts.Add(cost);
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
            VsMedian: vsMedian, Position: position,
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
