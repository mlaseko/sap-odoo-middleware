using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using SapOdooMiddleware.Integrations.Classifier;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Pricing;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Pages.Pricing;

/// <summary>
/// Operator UI for Lubes price management: EUR→TZS rate, single-item reprice
/// (lookup → preview diff → post), Excel/filter bulk reprice, and price history.
/// All work goes through <see cref="ILubesRepriceService"/> — identical behavior
/// to the /api/pricing endpoints.
/// </summary>
public class IndexModel : PageModel
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const int BulkPreviewCap = 500;

    private readonly ILubesPricingRepository _pricingRepo;
    private readonly ILubesRepriceService _reprice;
    private readonly LubesBulkRepriceJobService _bulkJobs;
    private readonly ISapB1Service _sap;
    private readonly IOdooService _odoo;
    private readonly INeonProductRepository _neon;
    private readonly ICategoryTaxonomy _taxonomy;
    private readonly IMemoryCache _cache;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        ILubesPricingRepository pricingRepo,
        ILubesRepriceService reprice,
        LubesBulkRepriceJobService bulkJobs,
        ISapB1Service sap,
        IOdooService odoo,
        INeonProductRepository neon,
        ICategoryTaxonomy taxonomy,
        IMemoryCache cache,
        ILogger<IndexModel> logger)
    {
        _pricingRepo = pricingRepo;
        _reprice = reprice;
        _bulkJobs = bulkJobs;
        _sap = sap;
        _odoo = odoo;
        _neon = neon;
        _taxonomy = taxonomy;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>SAP item groups (OITB) for the classification dropdown, cached 10 min.</summary>
    public List<(int Code, string Name)> SapGroups { get; private set; } = new();

    /// <summary>Odoo category full paths for the classification datalist.</summary>
    public IReadOnlyList<CategoryEntry> OdooCategories { get; private set; } = Array.Empty<CategoryEntry>();

    private async Task LoadClassificationSourcesAsync(CancellationToken ct)
    {
        try
        {
            SapGroups = (await _cache.GetOrCreateAsync("lubes-sap-item-groups", async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
                return await _sap.GetItemGroupsAsync(ct);
            }))!;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load SAP item groups — classification editor disabled this render.");
            SapGroups = new();
        }
        OdooCategories = _taxonomy.All();
    }

    public async Task<IActionResult> OnPostReclassifyAsync(
        string itemCode, int groupCode, string? odooCategoryPath, CancellationToken ct)
    {
        Rate = await _pricingRepo.GetEffectiveRateAsync(ct);
        LookedUpItem = itemCode.Trim();
        await LoadClassificationSourcesAsync(ct);

        try
        {
            var groupName = SapGroups.FirstOrDefault(g => g.Code == groupCode).Name;
            var path = string.IsNullOrWhiteSpace(odooCategoryPath) ? null : odooCategoryPath.Trim();
            var extId = path is null
                ? null
                : OdooCategories.FirstOrDefault(c =>
                    string.Equals(c.FullPath, path, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.Name, path, StringComparison.OrdinalIgnoreCase))?.ExternalId;

            // 1. SAP (authoritative for the pricing band).
            await _sap.SetItemClassificationAsync(LookedUpItem, groupCode, path, ct);

            // 2. Neon mirror.
            await _neon.UpdateClassificationAsync(LookedUpItem, groupCode, groupName, path, extId, ct);

            // 3. Odoo — best-effort, matched by external id first (naming-independent).
            var notes = new List<string>();
            if (path is not null)
            {
                try { notes = await _odoo.UpdateLubesCategoryAsync(LookedUpItem, path, extId); }
                catch (Exception ex) { notes.Add($"Odoo category update failed: {ex.Message}"); }
            }

            Message = $"✅ {LookedUpItem} reclassified to SAP group {groupCode} ({groupName}). "
                      + string.Join(" ", notes)
                      + " If the pricing band changed, run a reprice preview to update the prices.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }

        Preview = await _reprice.PreviewAsync(LookedUpItem, null, null, includeTrace: false, ct);
        EnteredEurCost = Preview.EurCost;
        History = await _pricingRepo.GetHistoryAsync(LookedUpItem, 10, ct);
        return Page();
    }

    // ── View state ───────────────────────────────────────────────────
    public EffectiveRate Rate { get; private set; } = new(0m, "", null);
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public RepricePreviewLine? Preview { get; private set; }
    public IReadOnlyList<PriceChangeEntry> History { get; private set; } = Array.Empty<PriceChangeEntry>();
    public string? LookedUpItem { get; private set; }
    public decimal? EnteredEurCost { get; private set; }

    public List<RepricePreviewLine> BulkLines { get; private set; } = new();
    public List<string> BulkParseErrors { get; private set; } = new();
    public string? PendingBulkJson { get; private set; }
    public decimal? BulkRate { get; private set; }

    public BulkRepriceJob? Job => _bulkJobs.Current;

    public static readonly string[] CategoryOptions =
    {
        "Engine Oils", "Additives", "Gear Oils & Transmission Fluids", "Greases",
        "Oils (Industrial/Other Fluids)", "Service", "Vehicle Care",
        "Workshop Pro-Line", "Pastes", "Adhesives & Sealants", "Repair Aids",
    };

    public async Task OnGetAsync(CancellationToken ct)
        => Rate = await _pricingRepo.GetEffectiveRateAsync(ct);

    /// <summary>
    /// AJAX autocomplete for the item and benchmark inputs
    /// (GET /pricing?handler=ItemSearch&amp;term=...): matches ItemCode OR ItemName.
    /// </summary>
    public async Task<IActionResult> OnGetItemSearchAsync(string? term, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 2)
            return new JsonResult(Array.Empty<object>());
        var hits = await _neon.SearchProductsAsync(term.Trim(), 15, ct);
        return new JsonResult(hits.Select(h => new { code = h.ItemCode, name = h.ItemName }));
    }

    // ── Rate ─────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostRateAsync(decimal newRate, CancellationToken ct)
    {
        if (newRate <= 0m)
            Error = "Rate must be greater than zero.";
        else
        {
            await _pricingRepo.SetRateAsync(newRate, ct);
            _logger.LogInformation("EUR→TZS rate set to {Rate} via /pricing UI.", newRate);
            Message = $"EUR→TZS rate set to {newRate:N0}. Provisioning and repricing use it immediately.";
        }
        Rate = await _pricingRepo.GetEffectiveRateAsync(ct);
        return Page();
    }

    // ── Single item ──────────────────────────────────────────────────

    public async Task<IActionResult> OnPostLookupAsync(
        string itemCode, string? compareItem, CancellationToken ct)
    {
        Rate = await _pricingRepo.GetEffectiveRateAsync(ct);
        if (string.IsNullOrWhiteSpace(itemCode)) { Error = "Enter an item code or name."; return Page(); }

        LookedUpItem = await ResolveItemInputAsync(itemCode.Trim(), ct);
        Preview = await _reprice.PreviewAsync(LookedUpItem, null, null, includeTrace: false, ct);
        EnteredEurCost = Preview.EurCost;
        History = await _pricingRepo.GetHistoryAsync(LookedUpItem, 10, ct);
        await LoadClassificationSourcesAsync(ct);
        await LoadCompareAsync(compareItem, ct);
        return Page();
    }

    /// <summary>
    /// Accepts a code OR a (partial) name: when the input isn't an exact item code,
    /// a unique search match is used automatically; several matches surface as a
    /// "did you mean" list.
    /// </summary>
    private async Task<string> ResolveItemInputAsync(string input, CancellationToken ct)
    {
        if (await _neon.GetPricingSnapshotAsync(input, ct) is not null)
            return input;   // exact item code

        var hits = await _neon.SearchProductsAsync(input, 6, ct);
        if (hits.Count == 1)
        {
            Message = $"Resolved '{input}' → {hits[0].ItemCode} — {hits[0].ItemName}.";
            return hits[0].ItemCode;
        }
        if (hits.Count > 1)
        {
            Error = $"'{input}' matches several items — did you mean: "
                    + string.Join(" · ", hits.Select(h => $"{h.ItemCode} ({h.ItemName})"))
                    + "? Pick one from the suggestions.";
        }
        return input;   // fall through — preview will report not-found (or the error above shows)
    }

    /// <summary>Sticky state for target-price mode.</summary>
    public int? TargetPl { get; private set; }
    public decimal? TargetPrice { get; private set; }
    public decimal? ImpliedEur { get; private set; }

    /// <summary>Benchmark item whose price lists are shown alongside the review.</summary>
    public string? CompareItem { get; private set; }
    public RepricePreviewLine? ComparePreview { get; private set; }

    private async Task LoadCompareAsync(string? compareItem, CancellationToken ct)
    {
        CompareItem = string.IsNullOrWhiteSpace(compareItem) ? null : compareItem.Trim();
        if (CompareItem is null) return;

        // A name (unique match) works too, not just an exact code.
        if (await _neon.GetPricingSnapshotAsync(CompareItem, ct) is null)
        {
            var hits = await _neon.SearchProductsAsync(CompareItem, 2, ct);
            if (hits.Count == 1) CompareItem = hits[0].ItemCode;
        }
        ComparePreview = await _reprice.PreviewAsync(CompareItem, null, null, includeTrace: false, ct);
    }

    public async Task<IActionResult> OnPostTargetPreviewAsync(
        string itemCode, int targetPl, decimal targetPrice, string? compareItem, CancellationToken ct)
    {
        Rate = await _pricingRepo.GetEffectiveRateAsync(ct);
        LookedUpItem = itemCode.Trim();
        TargetPl = targetPl;
        TargetPrice = targetPrice;

        if (targetPl is < 1 or > 4 || targetPrice <= 0m)
        {
            Error = "Pick a price list and enter a positive target price (incl VAT).";
        }
        else
        {
            var (implied, preview) = await _reprice.PreviewFromTargetAsync(
                LookedUpItem, targetPl, targetPrice, null, ct);
            Preview = preview;
            ImpliedEur = implied;
            EnteredEurCost = implied;
            if (implied is not null && preview.CanApply)
                Message = $"Solved: PL{targetPl} target {targetPrice:N0} ⇒ implied EUR invoice price €{implied:N2}. " +
                          "Preview only — review all four tiers, then Confirm & Post.";
            else
                Error ??= preview.Error;
        }

        History = await _pricingRepo.GetHistoryAsync(LookedUpItem, 10, ct);
        await LoadClassificationSourcesAsync(ct);
        await LoadCompareAsync(compareItem, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostPreviewAsync(
        string itemCode, decimal eurCost, string? compareItem, CancellationToken ct)
    {
        Rate = await _pricingRepo.GetEffectiveRateAsync(ct);
        LookedUpItem = itemCode.Trim();
        EnteredEurCost = eurCost;
        Preview = await _reprice.PreviewAsync(LookedUpItem, eurCost, null, includeTrace: false, ct);
        History = await _pricingRepo.GetHistoryAsync(LookedUpItem, 10, ct);
        await LoadCompareAsync(compareItem, ct);
        if (Preview.CanApply)
            Message = "Preview only — nothing posted yet. Review the diff, then Confirm & Post.";
        return Page();
    }

    public async Task<IActionResult> OnPostApplyAsync(
        string itemCode, decimal eurCost, string? note, string? compareItem, CancellationToken ct)
    {
        Rate = await _pricingRepo.GetEffectiveRateAsync(ct);
        LookedUpItem = itemCode.Trim();
        EnteredEurCost = eurCost;

        var result = await _reprice.ApplyAsync(LookedUpItem, eurCost, null, "manual", note, ct);
        if (result.Applied)
        {
            Message = $"✅ {LookedUpItem} repriced and posted to SAP + Neon. "
                      + string.Join(" ", result.OdooNotes);
        }
        else
        {
            Error = result.Error;
        }

        Preview = await _reprice.PreviewAsync(LookedUpItem, null, null, includeTrace: false, ct);
        History = await _pricingRepo.GetHistoryAsync(LookedUpItem, 10, ct);
        await LoadCompareAsync(compareItem, ct);
        return Page();
    }

    // ── Bulk ─────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostUploadAsync(
        IFormFile? file, decimal? bulkRate, CancellationToken ct)
    {
        Rate = await _pricingRepo.GetEffectiveRateAsync(ct);
        BulkRate = bulkRate;
        if (file is null || file.Length == 0) { Error = "Choose an .xlsx file first."; return Page(); }

        try
        {
            var (rows, errors) = ExcelRepriceParser.Parse(file.OpenReadStream());
            BulkParseErrors = errors;
            var items = rows.Select(r => new RepriceItemInput(r.ItemCode, r.EurCost)).ToList();
            await BuildBulkPreviewAsync(items, bulkRate, ct);
        }
        catch (Exception ex)
        {
            Error = $"Could not read the Excel file: {ex.Message}";
        }
        return Page();
    }

    public async Task<IActionResult> OnPostFilterPreviewAsync(
        string category, decimal? bulkRate, CancellationToken ct)
    {
        Rate = await _pricingRepo.GetEffectiveRateAsync(ct);
        BulkRate = bulkRate;

        // Category → SAP group codes (same mapping the calculator owns).
        var calc = HttpContext.RequestServices.GetRequiredService<IPricingCalculator>();
        var groups = new List<int>();
        for (int code = 100; code <= 130; code++)
            if (string.Equals(calc.TryPricingBandForSapGroup(code), category, StringComparison.OrdinalIgnoreCase))
                groups.Add(code);
        if (groups.Count == 0) { Error = $"No SAP groups map to band '{category}'."; return Page(); }

        var candidates = await _pricingRepo.GetRepriceCandidatesAsync(groups, ct);
        if (candidates.Count == 0)
        {
            Error = $"No items in '{category}' carry a stored EUR cost yet — items get a stored cost when "
                    + "provisioned or repriced. Use the Excel upload for these.";
            return Page();
        }
        var items = candidates.Select(c => new RepriceItemInput(c.ItemCode, c.LastEurCost)).ToList();
        await BuildBulkPreviewAsync(items, bulkRate, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostBulkApplyAsync(
        string pendingJson, decimal? bulkRate, string? note, CancellationToken ct)
    {
        Rate = await _pricingRepo.GetEffectiveRateAsync(ct);
        try
        {
            var items = JsonSerializer.Deserialize<List<RepriceItemInput>>(pendingJson, JsonOpts);
            if (items is null || items.Count == 0) { Error = "Nothing pending to apply — preview first."; return Page(); }

            var job = _bulkJobs.Start(items, bulkRate, "ui", note);
            Message = $"Bulk reprice started: {items.Count} items (job {job.JobId:N}). Refresh this page for progress.";
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }
        catch (JsonException)
        {
            Error = "Pending list was corrupted — re-run the preview.";
        }
        return Page();
    }

    public async Task<IActionResult> OnPostStopJobAsync(CancellationToken ct)
    {
        Rate = await _pricingRepo.GetEffectiveRateAsync(ct);
        Message = _bulkJobs.Stop()
            ? "Stop requested — the job halts after the current item."
            : "No bulk job is running.";
        return Page();
    }

    private async Task BuildBulkPreviewAsync(
        List<RepriceItemInput> items, decimal? bulkRate, CancellationToken ct)
    {
        if (items.Count == 0) { Error = "No usable rows found."; return; }
        if (items.Count > BulkPreviewCap)
        {
            Error = $"{items.Count} items exceed the {BulkPreviewCap}-item preview cap — split the file or narrow the filter.";
            return;
        }

        foreach (var i in items)
            BulkLines.Add(await _reprice.PreviewAsync(i.ItemCode, i.EurCost, bulkRate, includeTrace: false, ct));

        // Only applicable lines get carried into the apply step.
        var pending = items
            .Where(i => BulkLines.First(l => l.ItemCode == i.ItemCode).CanApply)
            .ToList();
        PendingBulkJson = JsonSerializer.Serialize(pending, JsonOpts);
        Message = $"Preview only — {pending.Count} of {items.Count} lines are ready to apply. Nothing posted yet.";
    }
}
