using MolasLubes.Infrastructure.Integrations.LiquiMoly;
using SapOdooMiddleware.Integrations.Classifier;

namespace SapOdooMiddleware.ItemProvisioning;

/// <summary>
/// Inquiry preview: the read-only front half of <see cref="ProvisionAsync"/> (scrape → classify), reusing the same
/// Layer-1 rules and DGX calls so the preview shows exactly what provisioning would decide. Differences:
///   - no price, no SAP / NeonProducts write (the Liqui Moly scrape cache upsert is the only write, as in provisioning);
///   - a low-confidence or failed classification is reported, not turned into an early "needs_review" exit;
///   - with no brand given, a code Liqui Moly does not know is tried on Meguin.
/// </summary>
public partial class LubesItemProvisioningService
{
    public async Task<LubesItemPreviewResult> PreviewAsync(LubesItemPreviewRequest req, CancellationToken ct)
    {
        var code = req.ArticleNumber?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(code))
            return new LubesItemPreviewResult("not_found", "", Message: "article_number is required.");

        var (lm, brand) = await FindForPreviewAsync(code, req.Brand, ct);
        if (lm is null)
            return new LubesItemPreviewResult("not_found", code, Brand: brand,
                Message: $"{brand} has no product with article number {code}.");

        var description = string.IsNullOrWhiteSpace(lm.Description)
            ? $"{code}-{lm.Name}."
            : $"{code}-{lm.Name}. {lm.Description}";
        var categoryHint = LubesCategoryRules.BuildCategoryHint(lm.Category, lm.SubCategory, lm.Name);
        var notes = new List<string>();

        // Odoo category: Layer-1 rule, else DGX /classify.
        CategoryClassification? cat = null;
        string? catSource = null;
        if (MatchOdooCategoryOverride(lm.Name) is { } co)
        {
            cat = new CategoryClassification { ExternalId = co.ExternalId, Name = co.Name, Confidence = 1.0 };
            catSource = "rule";
        }
        else
        {
            try
            {
                cat = await _classifier.ClassifyCategoryAsync(description, categoryHint, ct);
                catSource = "classifier";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Preview {Code}: classifier (/classify) unavailable", code);
                notes.Add("Classifier unavailable: no Odoo category predicted.");
            }
        }

        // SAP item group: Layer-1 rule, else DGX /classify_family.
        FamilyClassification? fam = null;
        string? famSource = null;
        if (MatchFamilyOverride(lm.Name) is { } fo)
        {
            fam = new FamilyClassification { GroupCode = fo.Code, GroupName = fo.Name, Confidence = 1.0 };
            famSource = "rule";
        }
        else
        {
            try
            {
                fam = await _classifier.ClassifyFamilyAsync(description, lm.Category, ct);
                famSource = "classifier";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Preview {Code}: classifier (/classify_family) unavailable", code);
                notes.Add("Classifier unavailable: no SAP item group predicted.");
            }
        }

        // The same checks provisioning applies, reported instead of enforced.
        var needsReview = cat is null || fam is null;
        if (cat is not null)
        {
            if (string.IsNullOrEmpty(cat.ExternalId) || cat.NeedsReview)
            {
                needsReview = true;
                notes.Add($"Low confidence on the Odoo category ({cat.Confidence:F2}).");
            }
            else if (LubesCategoryRules.CategoryProblem(cat.ExternalId) is { } problem)
            {
                needsReview = true;
                notes.Add($"Odoo category not precise: {problem}.");
            }
        }
        if (fam is not null)
        {
            if (fam.GroupCode is null)
            {
                needsReview = true;
                notes.Add($"No SAP item group returned ({fam.Confidence:F2}).");
            }
            else if (fam.NeedsReview && fam.Confidence < MinFamilyConfidence)
            {
                needsReview = true;
                notes.Add($"Low confidence on the SAP item group ({fam.Confidence:F2}).");
            }
        }
        if (cat?.ExternalId is { Length: > 0 } && fam?.GroupCode is not null
            && LubesCategoryRules.GroupMismatch(cat.ExternalId, fam.GroupCode) is { } mismatch)
        {
            needsReview = true;
            notes.Add(mismatch + ".");
        }

        var itemName = $"{code}-{lm.Name}";
        if (!string.IsNullOrWhiteSpace(lm.PackagingSize))
            itemName += $"-{lm.PackagingSize.Trim()}";
        if (itemName.Length > 200) itemName = itemName.Substring(0, 200);

        return new LubesItemPreviewResult(
            Status: "found",
            ItemCode: code,
            Brand: brand,
            ItemName: itemName,
            Product: lm,
            OdooCategoryName: cat?.Name,
            OdooCategoryExternalId: cat?.ExternalId,
            OdooCategoryFullPath: _taxonomy.FullPathFor(cat?.ExternalId) ?? cat?.Name,
            OdooCategoryConfidence: cat?.Confidence,
            OdooCategorySource: catSource,
            SapItemGroupCode: fam?.GroupCode,
            SapItemGroupName: fam?.GroupName,
            SapItemGroupConfidence: fam?.Confidence,
            SapItemGroupSource: famSource,
            NeedsReview: needsReview,
            ClassificationNote: notes.Count > 0 ? string.Join(" ", notes) : null,
            Candidates: cat?.Candidates is { Count: > 0 } c ? c : null);
    }

    /// <summary>
    /// The scraped product: the Neon cache first (as provisioning does), else a live scrape that is then cached.
    /// "meguin" asks Meguin only; "liqui_moly" Liqui Moly only; no brand tries Liqui Moly then Meguin.
    /// </summary>
    private async Task<(LiquiMolyProductDto? Product, string Brand)> FindForPreviewAsync(
        string code, string? brand, CancellationToken ct)
    {
        var wanted = (brand ?? "").Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
        var onlyMeguin = wanted == "meguin";
        var onlyLm = wanted is "liqui_moly" or "liquimoly";
        var label = onlyMeguin ? "Meguin" : "Liqui Moly";

        var cached = await _lmRepo.GetByArticleNumberAsync(code, ct);
        if (cached is not null)
            return (cached, label);

        var sources = new List<(LiquiMolyProductScraperService Scraper, string Brand)>();
        if (!onlyMeguin) sources.Add((_scraper, "Liqui Moly"));
        if (!onlyLm) sources.Add((_meguinScraper, "Meguin"));

        foreach (var (scraper, name) in sources)
        {
            var scraped = await scraper.ScrapeByArticleNumbersAsync(new[] { code }, ct);
            if (scraped is { Count: > 0 })
            {
                await _lmRepo.UpsertAsync(scraped[0], ct);
                return (scraped[0], name);
            }
        }
        return (null, label);
    }
}
