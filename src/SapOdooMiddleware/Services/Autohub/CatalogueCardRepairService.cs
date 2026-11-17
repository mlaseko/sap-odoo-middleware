using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// What the repair does (or did) for one SAP item whose catalogue card describes another part.
/// Action: <c>link</c> (move the item code to the existing free card of its real article), <c>create</c>
/// (make a new card for its real article, with the OEM numbers from its SAP item name), <c>duplicate</c>
/// (the real article's card belongs to another SAP item: two SAP items for one part, left for a person),
/// or <c>skip</c> (nothing to do; see Reason). Result after apply: done, skipped or failed.
/// </summary>
public sealed record CardRepairItem(
    string ItemCode, string? Brand, string SapArticle, string? WrongArticle, string? WrongName, long WrongOitmId,
    string Action, long? CorrectOitmId, string? DuplicateItemCode, IReadOnlyList<string> Oems, string? Reason,
    string? Result = null, long? NewOitmId = null);

public sealed record CardRepairReport(
    Guid? RunId, bool Applied, IReadOnlyList<string> Brands, int Link, int Create, int Duplicate, int Skip,
    int Done, int Failed, IReadOnlyList<CardRepairItem> Items);

/// <summary>
/// OEM numbers to add to a card made by the middleware (repair or Bulk Create's own card) that has none,
/// read from the SAP Item Description. Unreadable: parts left out because several numbers run together.
/// </summary>
public sealed record CardOemFill(
    long OitmId, string ItemCode, string? Article, string? ItemDescription,
    IReadOnlyList<string> Oems, IReadOnlyList<string> Unreadable, string? Result = null, int Added = 0);

public sealed record CardOemFillReport(bool Applied, int Cards, int WithOems, int StillNone, int Added,
    IReadOnlyList<CardOemFill> Items);

public interface ICatalogueCardRepairService
{
    /// <summary>Cards made by the middleware with no OEM number, and the OEMs their SAP description gives. Changes nothing.</summary>
    Task<CardOemFillReport> PreviewOemFillAsync(CancellationToken ct);

    /// <summary>Adds those OEM numbers (never removes any).</summary>
    Task<CardOemFillReport> ApplyOemFillAsync(CancellationToken ct);

    /// <summary>What the repair would do for the brands (first word of the SAP brand). Changes nothing.</summary>
    Task<CardRepairReport> PreviewAsync(IReadOnlyCollection<string> brands, CancellationToken ct);

    /// <summary>Does it: every link and create, each in its own transaction, recorded under one run id.</summary>
    Task<CardRepairReport> ApplyAsync(IReadOnlyCollection<string> brands, string user, CancellationToken ct);

    /// <summary>Puts a run back. Returns how many items were restored.</summary>
    Task<int> UndoAsync(Guid runId, CancellationToken ct);
}

/// <summary>
/// One-time repair of catalogue cards stamped with the wrong SAP item (see PartsItemProvisioningService.
/// DecideBridge for the cause). SAP decides what an item is: its OITM.U_Article_No, read live, and the
/// OEM numbers in its item name ("OEM1/OEM2/…/article"). Only Neon's catalogue changes; SAP is read only.
/// </summary>
public sealed class CatalogueCardRepairService : ICatalogueCardRepairService
{
    private readonly ICatalogueCardRepairRepository _repo;
    private readonly IAutohubInventorySqlService _sap;
    private readonly ILogger<CatalogueCardRepairService> _logger;

    public CatalogueCardRepairService(ICatalogueCardRepairRepository repo, IAutohubInventorySqlService sap,
        ILogger<CatalogueCardRepairService> logger)
    {
        _repo = repo;
        _sap = sap;
        _logger = logger;
    }

    public async Task<CardRepairReport> PreviewAsync(IReadOnlyCollection<string> brands, CancellationToken ct)
    {
        var plan = await PlanAsync(brands, ct);
        return Report(null, false, brands, plan);
    }

    public async Task<CardRepairReport> ApplyAsync(IReadOnlyCollection<string> brands, string user, CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        var plan = await PlanAsync(brands, ct);
        var cards = (await _repo.ListMislinkedAsync(brands, ct)).ToDictionary(c => c.WrongOitmId);
        var done = new List<CardRepairItem>(plan.Count);
        foreach (var item in plan)
        {
            if (item.Action is not ("link" or "create") || !cards.TryGetValue(item.WrongOitmId, out var card))
            {
                done.Add(item with { Result = "skipped" });
                continue;
            }
            try
            {
                if (item.Action == "link")
                {
                    var ok = await _repo.MoveToCorrectCardAsync(runId, card, item.SapArticle, user, ct);
                    done.Add(item with { Result = ok ? "done" : "skipped",
                        Reason = ok ? item.Reason : "Changed since the preview (card no longer carries the code, or the correct card was taken)." });
                }
                else
                {
                    var newId = await _repo.MoveToNewCardAsync(runId, card, item.SapArticle, item.Oems, user, ct);
                    done.Add(item with { Result = newId is null ? "skipped" : "done", NewOitmId = newId,
                        Reason = newId is null ? "Changed since the preview (card no longer carries the code)." : item.Reason });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Catalogue card repair failed for {ItemCode} (oitm {OitmId}).", item.ItemCode, item.WrongOitmId);
                done.Add(item with { Result = "failed", Reason = ex.Message });
            }
        }
        _logger.LogInformation("Catalogue card repair run {RunId} by {User}: {Done} done, {Failed} failed of {Total}.",
            runId, user, done.Count(d => d.Result == "done"), done.Count(d => d.Result == "failed"), done.Count);
        return Report(runId, true, brands, done);
    }

    public Task<int> UndoAsync(Guid runId, CancellationToken ct) => _repo.UndoRunAsync(runId, ct);

    public async Task<CardOemFillReport> PreviewOemFillAsync(CancellationToken ct) =>
        FillReport(false, await PlanOemFillAsync(ct));

    public async Task<CardOemFillReport> ApplyOemFillAsync(CancellationToken ct)
    {
        var done = new List<CardOemFill>();
        foreach (var item in await PlanOemFillAsync(ct))
        {
            if (item.Oems.Count == 0) { done.Add(item with { Result = "none found" }); continue; }
            try
            {
                var added = await _repo.AddOemsAsync(item.OitmId, item.Oems, ct);
                done.Add(item with { Result = "done", Added = added });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Adding OEMs to card {OitmId} ({ItemCode}) failed.", item.OitmId, item.ItemCode);
                done.Add(item with { Result = "failed: " + ex.Message });
            }
        }
        return FillReport(true, done);
    }

    private async Task<List<CardOemFill>> PlanOemFillAsync(CancellationToken ct)
    {
        var cards = await _repo.ListCardsWithoutOemsAsync(ct);
        var sap = await _sap.GetComparableItemsAsync(cards.Select(c => c.ItemCode).Distinct().ToList(), ct);
        return cards.Select(c =>
        {
            sap.TryGetValue(c.ItemCode, out var s);
            var article = s?.ArticleNumber ?? c.Article ?? "";
            var read = ReadDescription(s?.ItemName, article);
            return new CardOemFill(c.OitmId, c.ItemCode, article, s?.ItemName, read.Oems, read.Unreadable);
        }).ToList();
    }

    private static CardOemFillReport FillReport(bool applied, IReadOnlyList<CardOemFill> items) =>
        new(applied, items.Count, items.Count(i => i.Oems.Count > 0), items.Count(i => i.Oems.Count == 0),
            items.Sum(i => i.Added), items);

    private async Task<List<CardRepairItem>> PlanAsync(IReadOnlyCollection<string> brands, CancellationToken ct)
    {
        var cards = await _repo.ListMislinkedAsync(brands, ct);
        var sap = await _sap.GetComparableItemsAsync(cards.Select(c => c.ItemCode).Distinct().ToList(), ct);
        return cards.Select(c => Plan(c, sap.TryGetValue(c.ItemCode, out var s) ? s : null)).ToList();
    }

    /// <summary>The repair for one card, from what SAP says the item is. Pure, so it is unit-tested.</summary>
    internal static CardRepairItem Plan(MislinkedCard c, ComparableSapItem? sap)
    {
        CardRepairItem Item(string action, string sapArticle, string? reason, IReadOnlyList<string>? oems = null,
            long? correct = null, string? duplicate = null) =>
            new(c.ItemCode, c.SapBrand, sapArticle, c.WrongArticle, c.WrongName, c.WrongOitmId, action, correct, duplicate,
                oems ?? Array.Empty<string>(), reason);

        if (sap is null)
            return Item("skip", c.MirrorArticle, "Item not found in SAP.");
        var sapArticle = sap.ArticleNumber ?? c.MirrorArticle;
        if (!ArticleNumbers.Differ(c.WrongArticle, sapArticle))
            return Item("skip", sapArticle, "The card already has SAP's article (the mirror is out of date).");

        // The correct card was looked up by the mirror's article; trust it only when SAP agrees.
        bool lookupValid = !ArticleNumbers.Differ(c.MirrorArticle, sapArticle);
        if (lookupValid && c.CorrectOitmId is { } correct)
        {
            if (string.IsNullOrWhiteSpace(c.CorrectItemCode) || string.Equals(c.CorrectItemCode, c.ItemCode, StringComparison.OrdinalIgnoreCase))
                return Item("link", sapArticle, $"Card {correct} already describes {sapArticle}.", correct: correct);
            return Item("duplicate", sapArticle,
                $"The card of {sapArticle} belongs to {c.CorrectItemCode}: two SAP items for one part. Decide which to keep.",
                correct: correct, duplicate: c.CorrectItemCode);
        }

        var oems = OemsFromItemName(sap.ItemName, sapArticle);
        return Item("create", sapArticle,
            oems.Count == 0 ? "No OEM number in the SAP item name: the new card has none." : null, oems);
    }

    /// <summary>The OEM numbers of a SAP item, and the parts of its description that could not be read safely.</summary>
    public sealed record DescriptionOems(IReadOnlyList<string> Oems, IReadOnlyList<string> Unreadable);

    /// <summary>A part longer than this is several numbers run together without "/" (e.g. "1K0121367F1KD121367D…").</summary>
    internal const int MaxOemLength = 20;

    /// <summary>
    /// The OEM numbers in a SAP Item Description (OITM.ItemName), which holds the OEM numbers and the article
    /// separated by "/" in any order ("B10045/5Q0411303P/…" or "…/06J115105AC/B12698"). Each part: its first
    /// word ("4M0853651Q RN4" → 4M0853651Q), without a trailing "kit" ("03H109503kit" → 03H109503); kept when
    /// it has at least 5 characters and a digit. Left out: the article and anything starting with it (a variant
    /// such as B1002803 for B10028). A part longer than <see cref="MaxOemLength"/> is several numbers run
    /// together: not guessed at, returned as Unreadable so the description can be fixed in SAP.
    /// </summary>
    internal static DescriptionOems ReadDescription(string? description, string article)
    {
        if (string.IsNullOrWhiteSpace(description))
            return new(Array.Empty<string>(), Array.Empty<string>());
        var art = ArticleNumbers.Normalize(article);
        var oems = new List<string>();
        var unreadable = new List<string>();
        foreach (var part in description.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var word = part.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (word.Length > 8 && word.EndsWith("kit", StringComparison.OrdinalIgnoreCase))
                word = word[..^3];
            var n = ArticleNumbers.Normalize(word);
            if (n is null) continue;
            if (art is not null && n.StartsWith(art, StringComparison.Ordinal)) continue;
            if (word.Length > MaxOemLength) { unreadable.Add(part); continue; }
            if (word.Length < 5 || !word.Any(char.IsDigit) || !word.All(c => char.IsLetterOrDigit(c) || c is '-' or '.')) continue;
            var oem = word.ToUpperInvariant();
            if (!oems.Contains(oem)) oems.Add(oem);
        }
        return new(oems, unreadable);
    }

    /// <summary>The OEM numbers of <see cref="ReadDescription"/>.</summary>
    internal static IReadOnlyList<string> OemsFromItemName(string? itemName, string article) =>
        ReadDescription(itemName, article).Oems;

    private static CardRepairReport Report(Guid? runId, bool applied, IReadOnlyCollection<string> brands, IReadOnlyList<CardRepairItem> items) =>
        new(runId, applied, brands.ToList(),
            items.Count(i => i.Action == "link"), items.Count(i => i.Action == "create"),
            items.Count(i => i.Action == "duplicate"), items.Count(i => i.Action == "skip"),
            items.Count(i => i.Result == "done"), items.Count(i => i.Result == "failed"), items);
}
