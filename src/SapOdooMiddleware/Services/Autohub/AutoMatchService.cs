using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>Outcome of a single line's auto-match: Status ∈ {matched, skip, needs_confirmation, pending}.</summary>
public sealed record MatchDecision(string Status, string? ItemCode, string? MatchStrategy = null, OitmMatch? SuggestedDonor = null);

public interface IAutoMatchService
{
    Task<MatchDecision> DecideAsync(PartsLineMatchCandidate line, CancellationToken ct);
}

/// <summary>
/// Decision D2 auto-match, isolated from the worker host so it is unit-testable. Enforces supplier
/// identity (Slice 1.6): a SAP item represents exactly one (supplier, article), so:
///   promotional                                → skip
///   Tier 1 (OEM) hit, same supplier            → matched (tier1_oem)
///   Tier 1 (OEM) hit, vehicle-group / no brand → needs_confirmation (operator picks; donor suggested)
///   Tier 1 (OEM) hit, different supplier        → NOT matched — falls through (enrichment will create-new)
///   Tier 2 (article) hit                        → matched, unless a different *specific* supplier (then confirm)
///   otherwise                                   → pending (operator / enrichment decides)
/// OEMs are run through the Option-C filter first so position/engine noise never reaches the lookup.
/// </summary>
public sealed class AutoMatchService : IAutoMatchService
{
    private readonly IOitmMatchRepository _oitm;
    private readonly IOemFilterService _filter;

    public AutoMatchService(IOitmMatchRepository oitm, IOemFilterService filter)
    {
        _oitm = oitm;
        _filter = filter;
    }

    public async Task<MatchDecision> DecideAsync(PartsLineMatchCandidate line, CancellationToken ct)
    {
        if (line.IsPromotional)
            return new MatchDecision("skip", null);

        // Tier 1 — OEM cross-reference. Only matches across the SAME supplier; a shared OEM under a
        // different supplier must NOT auto-link (that was the Slice 1.6 bug).
        var clean = _filter.Filter(line.OemNumbers, line.SupplierArticleNumber, line.Brand).CleanOems;
        if (clean.Count > 0)
        {
            var oem = await _oitm.FindByOemAsync(clean, ct);
            if (oem is not null)
            {
                switch (BrandClassifier.Classify(line.Brand, oem.SupplierName))
                {
                    case BrandClassifier.MatchKind.SameSupplier:
                        return new MatchDecision("matched", oem.ItemCode, "tier1_oem");
                    case BrandClassifier.MatchKind.VehicleGroupBrand:
                    case BrandClassifier.MatchKind.NoBrandOnInvoice:
                        return new MatchDecision("needs_confirmation", null, "vehicle_group_brand_needs_confirmation", oem);
                    // DifferentSupplier: fall through — don't OEM-match across suppliers.
                }
            }
        }

        // Tier 2 — exact supplier article number. An exact article is a strong identity signal, so we
        // trust it unless the donor is a clearly different *specific* supplier.
        if (!string.IsNullOrWhiteSpace(line.SupplierArticleNumber))
        {
            var art = await _oitm.FindByArticleAsync(line.SupplierArticleNumber, ct);
            if (art is not null)
            {
                if (string.IsNullOrEmpty(art.SupplierName))
                    return new MatchDecision("matched", art.ItemCode, "tier2_article");

                switch (BrandClassifier.Classify(line.Brand, art.SupplierName))
                {
                    case BrandClassifier.MatchKind.SameSupplier:
                        return new MatchDecision("matched", art.ItemCode, "tier2_article");
                    default:
                        // Vehicle-group, no brand, or a different specific supplier on an exact-article
                        // collision — let the operator confirm rather than silently link.
                        return new MatchDecision("needs_confirmation", null, "vehicle_group_brand_needs_confirmation", art);
                }
            }
        }

        return new MatchDecision("pending", null);
    }
}
