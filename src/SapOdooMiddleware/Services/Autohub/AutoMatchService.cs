using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>Outcome of a single line's auto-match: Status ∈ {matched, skip, pending}.</summary>
public sealed record MatchDecision(string Status, string? ItemCode);

public interface IAutoMatchService
{
    Task<MatchDecision> DecideAsync(PartsLineMatchCandidate line, CancellationToken ct);
}

/// <summary>
/// Decision D2 auto-match, isolated from the worker host so it is unit-testable:
///   promotional      → skip
///   Tier 1 (OEM)     → match if any filtered OEM hits oitm_cross_reference
///   Tier 2 (article) → match if the supplier article hits oitm."U_Article_No"
///   otherwise        → pending (operator decides; Tier 3 fuzzy is out of scope)
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

        var clean = _filter.Filter(line.OemNumbers, line.SupplierArticleNumber, null).CleanOems;
        if (clean.Count > 0)
        {
            var byOem = await _oitm.FindItemCodeByOemAsync(clean, ct);
            if (byOem is not null)
                return new MatchDecision("matched", byOem);
        }

        if (!string.IsNullOrWhiteSpace(line.SupplierArticleNumber))
        {
            var byArticle = await _oitm.FindItemCodeByArticleAsync(line.SupplierArticleNumber, ct);
            if (byArticle is not null)
                return new MatchDecision("matched", byArticle);
        }

        return new MatchDecision("pending", null);
    }
}
