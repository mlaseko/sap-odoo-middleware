using System.Diagnostics;
using MolasLubes.Infrastructure.Integrations.LiquiMoly;
using SapOdooMiddleware.Integrations.Classifier;

namespace SapOdooMiddleware.ItemProvisioning;

/// <summary>
/// Inquiry preview: the read-only front half of <see cref="ProvisionAsync"/> (scrape → classify), reusing the same
/// Layer-1 rules and DGX calls so the preview shows exactly what provisioning would decide. Differences:
///   - no price, no SAP / NeonProducts write (the Liqui Moly scrape cache upsert is the only write, as in provisioning);
///   - a low-confidence, failed or too slow classification is reported, not turned into an early "needs_review" exit;
///   - the Odoo category and the SAP item group are asked of DGX at the same time, within
///     <see cref="PreviewClassifierBudget"/> (DGX answers one call in up to 180 s; a person is waiting here);
///   - classify=false stops after the scrape (the salesperson's fetch); Odoo classifies once the inquiry is saved;
///   - with no brand given, a code Liqui Moly does not know is tried on Meguin;
///   - every step is reported as it runs (<see cref="IItemPreviewProgress"/>), for the Odoo panel.
/// </summary>
public partial class LubesItemProvisioningService
{
    internal static readonly TimeSpan PreviewClassifierBudget = TimeSpan.FromSeconds(120);

    private const string StepLookup = "lookup", StepIndex = "index", StepScrape = "scrape", StepMeguin = "meguin",
                         StepCategory = "category", StepGroup = "group";
    private const string LabelLookup = "Look for saved Liqui Moly data";
    private const string LabelIndex = "Liqui Moly product list";
    private const string LabelScrape = "Read the Liqui Moly product page";
    private const string LabelMeguin = "Try Meguin";
    private const string LabelCategory = "Predict the Odoo category";
    private const string LabelGroup = "Predict the SAP item group";

    public Task<LubesItemPreviewResult> PreviewAsync(LubesItemPreviewRequest req, CancellationToken ct)
        => PreviewAsync(req, NullItemPreviewProgress.Instance, ct);

    public async Task<LubesItemPreviewResult> PreviewAsync(
        LubesItemPreviewRequest req, IItemPreviewProgress progress, CancellationToken ct)
    {
        var code = req.ArticleNumber?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(code))
            return new LubesItemPreviewResult("not_found", "", Message: "article_number is required.");

        // the whole plan up front, so the panel shows what is still to come
        progress.Step(StepLookup, LabelLookup, "pending");
        progress.Step(StepScrape, LabelScrape, "pending");
        if (req.Classify)
        {
            progress.Step(StepCategory, LabelCategory, "pending");
            progress.Step(StepGroup, LabelGroup, "pending");
        }

        var (lm, brand) = await FindForPreviewAsync(code, req.Brand, req.Refresh, progress, ct);
        if (lm is null)
        {
            if (req.Classify)
            {
                progress.Step(StepCategory, LabelCategory, "skipped", "no product to classify");
                progress.Step(StepGroup, LabelGroup, "skipped", "no product to classify");
            }
            return new LubesItemPreviewResult("not_found", code, Brand: brand,
                Message: $"{brand} has no product with article number {code}.");
        }

        // the gallery people see: this pack's pictures once each, front first (the cache keeps the scraper's list)
        var pictures = ProductImageGallery.Clean(new[] { lm.ImageUrl }.Concat(lm.AllImageUrls ?? new()), code);
        if (pictures.Count > 0)
        {
            lm.AllImageUrls = pictures;
            lm.ImageUrl = pictures[0];
        }

        var itemName = $"{code}-{lm.Name}";
        if (!string.IsNullOrWhiteSpace(lm.PackagingSize))
            itemName += $"-{lm.PackagingSize.Trim()}";
        if (itemName.Length > 200) itemName = itemName.Substring(0, 200);
        progress.Product(lm, brand, itemName);
        if (!req.Classify)
            return new LubesItemPreviewResult("found", code, Brand: brand, ItemName: itemName, Product: lm);

        var description = string.IsNullOrWhiteSpace(lm.Description)
            ? $"{code}-{lm.Name}."
            : $"{code}-{lm.Name}. {lm.Description}";
        var categoryHint = LubesCategoryRules.BuildCategoryHint(lm.Category, lm.SubCategory, lm.Name);
        var notes = new List<string>();

        // Both classifications at once, within the budget (each is a rule or one DGX call).
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(PreviewClassifierBudget);
        var catTask = PreviewCategoryAsync(code, lm.Name, description, categoryHint, progress, notes, budget.Token);
        var famTask = PreviewFamilyAsync(code, lm.Name, description, lm.Category, progress, notes, budget.Token);
        await Task.WhenAll(catTask, famTask);
        ct.ThrowIfCancellationRequested();
        var (cat, catSource) = catTask.Result;
        var (fam, famSource) = famTask.Result;

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
            ClassificationComplete: cat is not null && fam is not null,
            ClassificationNote: notes.Count > 0 ? string.Join(" ", notes) : null,
            Candidates: cat?.Candidates is { Count: > 0 } c ? c : null);
    }

    /// <summary>Odoo category: Layer-1 rule, else DGX /classify. (null, null) when DGX failed or ran out of time.</summary>
    private async Task<(CategoryClassification?, string?)> PreviewCategoryAsync(
        string code, string? name, string description, string? hint, IItemPreviewProgress progress,
        List<string> notes, CancellationToken ct)
    {
        if (MatchOdooCategoryOverride(name) is { } co)
        {
            progress.Step(StepCategory, LabelCategory, "done", $"{co.Name} (fixed rule)");
            return (new CategoryClassification { ExternalId = co.ExternalId, Name = co.Name, Confidence = 1.0 }, "rule");
        }
        progress.Step(StepCategory, LabelCategory, "running", "asking the classifier");
        try
        {
            var cat = await _classifier.ClassifyCategoryAsync(description, hint, ct);
            progress.Step(StepCategory, LabelCategory, "done",
                $"{_taxonomy.FullPathFor(cat.ExternalId) ?? cat.Name ?? "none"} ({cat.Confidence:P0})");
            return (cat, "classifier");
        }
        catch (Exception ex)
        {
            var slow = ct.IsCancellationRequested;
            _logger.LogWarning(ex, "Preview {Code}: classifier (/classify) {Why}", code, slow ? "too slow" : "unavailable");
            var why = slow ? $"no answer within {PreviewClassifierBudget.TotalSeconds:F0} s" : "classifier unavailable";
            progress.Step(StepCategory, LabelCategory, "failed", why);
            lock (notes) notes.Add($"Odoo category not predicted: {why}.");
            return (null, null);
        }
    }

    /// <summary>SAP item group: Layer-1 rule, else DGX /classify_family. (null, null) when DGX failed or ran out of time.</summary>
    private async Task<(FamilyClassification?, string?)> PreviewFamilyAsync(
        string code, string? name, string description, string? hint, IItemPreviewProgress progress,
        List<string> notes, CancellationToken ct)
    {
        if (MatchFamilyOverride(name) is { } fo)
        {
            progress.Step(StepGroup, LabelGroup, "done", $"{fo.Code} {fo.Name} (fixed rule)");
            return (new FamilyClassification { GroupCode = fo.Code, GroupName = fo.Name, Confidence = 1.0 }, "rule");
        }
        progress.Step(StepGroup, LabelGroup, "running", "asking the classifier");
        try
        {
            var fam = await _classifier.ClassifyFamilyAsync(description, hint, ct);
            progress.Step(StepGroup, LabelGroup, "done",
                fam.GroupCode is null ? "none" : $"{fam.GroupCode} {fam.GroupName} ({fam.Confidence:P0})");
            return (fam, "classifier");
        }
        catch (Exception ex)
        {
            var slow = ct.IsCancellationRequested;
            _logger.LogWarning(ex, "Preview {Code}: classifier (/classify_family) {Why}", code, slow ? "too slow" : "unavailable");
            var why = slow ? $"no answer within {PreviewClassifierBudget.TotalSeconds:F0} s" : "classifier unavailable";
            progress.Step(StepGroup, LabelGroup, "failed", why);
            lock (notes) notes.Add($"SAP item group not predicted: {why}.");
            return (null, null);
        }
    }

    /// <summary>
    /// The scraped product: the Neon cache first (as provisioning does), else a live scrape that is then cached.
    /// "meguin" asks Meguin only; "liqui_moly" Liqui Moly only; no brand tries Liqui Moly then Meguin.
    /// </summary>
    private async Task<(LiquiMolyProductDto? Product, string Brand)> FindForPreviewAsync(
        string code, string? brand, bool refresh, IItemPreviewProgress progress, CancellationToken ct)
    {
        var wanted = (brand ?? "").Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
        var onlyMeguin = wanted == "meguin";
        var onlyLm = wanted is "liqui_moly" or "liquimoly";
        var label = onlyMeguin ? "Meguin" : "Liqui Moly";

        progress.Step(StepLookup, LabelLookup, "running");
        var cached = refresh ? null : await _lmRepo.GetByArticleNumberAsync(code, ct);
        if (cached is not null)
        {
            var age = cached.ScrapedAt is { } at ? $", read {(int)(DateTime.UtcNow - at).TotalDays} day(s) ago" : "";
            progress.Step(StepLookup, LabelLookup, "done", $"found{age}");
            progress.Step(StepScrape, LabelScrape, "skipped", "already saved");
            return (cached, label);
        }
        progress.Step(StepLookup, LabelLookup, "done", refresh ? "reading the page again" : "not fetched before");

        var sources = new List<(LiquiMolyProductScraperService Scraper, string Brand, string Key, string Label)>();
        if (!onlyMeguin) sources.Add((_scraper, "Liqui Moly", StepScrape, LabelScrape));
        if (!onlyLm) sources.Add((_meguinScraper, "Meguin", onlyMeguin ? StepScrape : StepMeguin,
                                  onlyMeguin ? "Read the Meguin product page" : LabelMeguin));

        foreach (var (scraper, name, key, stepLabel) in sources)
        {
            var watch = Stopwatch.StartNew();
            if (!scraper.IsIndexWarm())
                progress.Step(StepIndex, $"{name} product list", "running",
                    "being rebuilt after a middleware restart: a few minutes, once a day at most");
            progress.Step(key, stepLabel, "running");
            var scraped = await scraper.ScrapeByArticleNumbersAsync(new[] { code }, ct);
            if (scraper.IsIndexWarm())
                progress.Step(StepIndex, $"{name} product list", "done", "ready");
            if (scraped is { Count: > 0 })
            {
                await _lmRepo.UpsertAsync(scraped[0], ct);
                progress.Step(key, stepLabel, "done", $"{scraped[0].Name} ({watch.Elapsed.TotalSeconds:F0} s)");
                return (scraped[0], name);
            }
            progress.Step(key, stepLabel, "failed", $"{name} does not list {code}");
        }
        return (null, label);
    }
}
