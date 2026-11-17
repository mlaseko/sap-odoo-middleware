using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Catalogue cards stamped with the wrong SAP item: Bulk Create now only stamps a card of the same article
/// (else it makes the item its own card), and the one-time repair moves each wrong code to the right card,
/// using SAP's article and the OEM numbers in the SAP item name.
/// </summary>
public class CatalogueCardRepairTests
{
    // ── Part 1: the check before stamping ──

    [Theory]
    [InlineData("B12746", "B12698", "OwnCard")]   // VAG12331: oil pump stamped on the timing chain's card
    [InlineData("B-12698", "b12698", "Link")]     // same article, written differently
    [InlineData(null, "B12698", "Link")]          // card without an article: linked as before
    [InlineData("B12698", null, "Link")]          // line without an article: linked as before
    public void Bulk_create_stamps_only_a_card_of_the_same_article(string? card, string? line, string expected) =>
        Assert.Equal(expected, PartsItemProvisioningService.DecideBridge(card, line).ToString());

    // ── Part 2: planning the repair ──

    private static MislinkedCard Card(string code = "VAG12331", string mirror = "B12698", string wrong = "B12746",
        long? correct = null, string? correctCode = null) =>
        new(2323, code, "BORSEHUNG", mirror, wrong, "Timing Chain", correct, correctCode);

    private static ComparableSapItem Sap(string article = "B12698", string? name = "06J115105AB/06J115105AC/B12698") =>
        new("VAG12331", 20000m, 450000m, 272872m, true, ArticleNumber: article, ItemName: name);

    [Fact]
    public void Free_correct_card_is_linked()
    {
        var p = CatalogueCardRepairService.Plan(Card(correct: 11526), Sap());
        Assert.Equal("link", p.Action);
        Assert.Equal(11526, p.CorrectOitmId);
    }

    [Fact]
    public void Correct_card_already_carrying_the_same_code_is_linked_too()
    {
        var p = CatalogueCardRepairService.Plan(Card(correct: 11526, correctCode: "VAG12331"), Sap());
        Assert.Equal("link", p.Action);
    }

    [Fact]
    public void Correct_card_of_another_item_is_a_duplicate_left_for_a_person()
    {
        var p = CatalogueCardRepairService.Plan(
            Card("VAG11902", "B15130", "B18294", correct: 11426, correctCode: "VAG15700"),
            Sap("B15130", "B15130") with { ItemCode = "VAG11902" });
        Assert.Equal("duplicate", p.Action);
        Assert.Equal("VAG15700", p.DuplicateItemCode);
        Assert.Contains("two SAP items for one part", p.Reason);
    }

    [Fact]
    public void No_correct_card_creates_one_with_the_OEMs_of_the_SAP_name()
    {
        var p = CatalogueCardRepairService.Plan(Card(), Sap());
        Assert.Equal("create", p.Action);
        Assert.Equal("B12698", p.SapArticle);
        Assert.Equal(new[] { "06J115105AB", "06J115105AC" }, p.Oems);
    }

    [Fact]
    public void Item_not_in_SAP_or_card_already_right_is_skipped()
    {
        Assert.Equal("skip", CatalogueCardRepairService.Plan(Card(), null).Action);
        var agrees = CatalogueCardRepairService.Plan(Card(wrong: "B12698"), Sap());
        Assert.Equal("skip", agrees.Action);
        Assert.Contains("already has SAP's article", agrees.Reason);
    }

    [Fact]
    public void When_SAP_and_the_mirror_disagree_the_mirror_lookup_is_not_trusted()
    {
        // Mirror still says B12746 (stale); SAP says B12698: the "correct" card found by the mirror is ignored.
        var p = CatalogueCardRepairService.Plan(Card(mirror: "B11111", wrong: "B12746", correct: 999), Sap());
        Assert.Equal("create", p.Action);
        Assert.Equal("B12698", p.SapArticle);
    }

    [Theory]
    [InlineData("06J115105AB/06J115105AC/B12698", "B12698", "06J115105AB|06J115105AC")]
    [InlineData("8K0698451A/8K0698451C/66981102701/GX-2", "66981102701", "8K0698451A|8K0698451C")]
    [InlineData("OIL PUMP KIT", "B12698", "")]                  // a hand-typed name: no OEM numbers
    [InlineData("12/AB/B12698", "B12698", "")]                   // too short / no digit
    [InlineData(null, "B12698", "")]
    public void OEMs_are_read_from_the_SAP_item_name(string? name, string article, string expected) =>
        Assert.Equal(expected, string.Join("|", CatalogueCardRepairService.OemsFromItemName(name, article)));

    // ── Applying ──

    [Fact]
    public async Task Apply_links_and_creates_under_one_run_and_leaves_duplicates()
    {
        var link = Card("VAG12331", correct: 11526) with { WrongOitmId = 1 };
        var create = Card("VAG10606", "B10218", "B12746") with { WrongOitmId = 2 };
        var dup = Card("VAG11902", "B15130", "B18294", correct: 11426, correctCode: "VAG15700") with { WrongOitmId = 3 };
        var gone = Card("VAG10613", "B10207", "B1C018") with { WrongOitmId = 4 };

        var repo = new Mock<ICatalogueCardRepairRepository>();
        repo.Setup(r => r.ListMislinkedAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { link, create, dup, gone });
        repo.Setup(r => r.MoveToCorrectCardAsync(It.IsAny<Guid>(), link, "B12698", "anna", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        repo.Setup(r => r.MoveToNewCardAsync(It.IsAny<Guid>(), create, "B10218", It.IsAny<IReadOnlyList<string>>(), "anna", It.IsAny<CancellationToken>()))
            .ReturnsAsync(5000L);
        repo.Setup(r => r.MoveToNewCardAsync(It.IsAny<Guid>(), gone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), "anna", It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);   // changed since the preview

        var sap = new Mock<IAutohubInventorySqlService>();
        sap.Setup(s => s.GetComparableItemsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new Dictionary<string, ComparableSapItem>(StringComparer.OrdinalIgnoreCase)
           {
               ["VAG12331"] = Sap(),
               ["VAG10606"] = new("VAG10606", null, null, null, true, "B10218", "06H109229AE/B10218"),
               ["VAG11902"] = new("VAG11902", null, null, null, true, "B15130", "B15130"),
               ["VAG10613"] = new("VAG10613", null, null, null, true, "B10207", "B10207"),
           });

        var report = await new CatalogueCardRepairService(repo.Object, sap.Object, NullLogger<CatalogueCardRepairService>.Instance)
            .ApplyAsync(new[] { "BORSEHUNG" }, "anna", CancellationToken.None);

        Assert.True(report.Applied);
        Assert.NotNull(report.RunId);
        Assert.Equal(2, report.Done);
        Assert.Equal("done", report.Items.Single(i => i.ItemCode == "VAG12331").Result);
        var created = report.Items.Single(i => i.ItemCode == "VAG10606");
        Assert.Equal(5000L, created.NewOitmId);
        Assert.Equal(new[] { "06H109229AE" }, created.Oems);
        Assert.Equal("skipped", report.Items.Single(i => i.ItemCode == "VAG11902").Result);
        Assert.Equal("skipped", report.Items.Single(i => i.ItemCode == "VAG10613").Result);
        repo.Verify(r => r.MoveToCorrectCardAsync(report.RunId!.Value, It.IsAny<MislinkedCard>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Reading OEM numbers from real SAP Item Descriptions (the 11 cards left without OEMs) ──

    [Theory]
    [InlineData("B10045/5Q0411303P/5Q0411303L/5Q0411303S", "B10045", "5Q0411303P|5Q0411303L|5Q0411303S", "")]
    [InlineData("B10218/06K115225C/06K109507G/06H105209AT", "B10218", "06K115225C|06K109507G|06H105209AT", "")]
    [InlineData("B10207/03H109503kit/03H109507/03H109465", "B10207", "03H109503|03H109507|03H109465", "")]
    [InlineData("B10220/06K198228Ckit/06H109158M/06H109467AR", "B10220", "06K198228C|06H109158M|06H109467AR", "")]
    [InlineData("1K0412303M/5Q0413175C/B1002803", "B10028", "1K0412303M|5Q0413175C", "")]
    [InlineData("4M0853651Q RN4/88531790102", "88531790102", "4M0853651Q", "")]
    [InlineData("99061388401/038906051C/7L6145795R/1J0145770L/04E906051", "99061388401", "038906051C|7L6145795R|1J0145770L|04E906051", "")]
    [InlineData("180121367/1K0121367A/1K0121367F1KD121367D1Z0121367AK11789601", "K11789601",
        "180121367|1K0121367A", "1K0121367F1KD121367D1Z0121367AK11789601")]
    public void Description_OEMs_are_read_in_any_order(string description, string article, string oems, string unreadable)
    {
        var r = CatalogueCardRepairService.ReadDescription(description, article);
        Assert.Equal(oems, string.Join("|", r.Oems));
        Assert.Equal(unreadable, string.Join("|", r.Unreadable));
    }

    [Fact]
    public async Task Oem_fill_adds_the_description_OEMs_to_cards_without_any()
    {
        var repo = new Mock<ICatalogueCardRepairRepository>();
        repo.Setup(r => r.ListCardsWithoutOemsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new CardWithoutOems(9001, "VAG10335", "B10045"),
            new CardWithoutOems(9002, "VAG99999", "X1"),
        });
        repo.Setup(r => r.AddOemsAsync(9001, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(3);
        var sap = new Mock<IAutohubInventorySqlService>();
        sap.Setup(s => s.GetComparableItemsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new Dictionary<string, ComparableSapItem>(StringComparer.OrdinalIgnoreCase)
           {
               ["VAG10335"] = new("VAG10335", null, null, null, true, "B10045", "B10045/5Q0411303P/5Q0411303L/5Q0411303S"),
               ["VAG99999"] = new("VAG99999", null, null, null, true, "X1", "PLAIN TEXT NAME"),
           });
        var svc = new CatalogueCardRepairService(repo.Object, sap.Object, NullLogger<CatalogueCardRepairService>.Instance);

        var preview = await svc.PreviewOemFillAsync(CancellationToken.None);
        Assert.False(preview.Applied);
        Assert.Equal(1, preview.WithOems);
        Assert.Equal(1, preview.StillNone);
        repo.Verify(r => r.AddOemsAsync(It.IsAny<long>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);

        var applied = await svc.ApplyOemFillAsync(CancellationToken.None);
        Assert.Equal(3, applied.Added);
        Assert.Equal("done", applied.Items.Single(i => i.ItemCode == "VAG10335").Result);
        Assert.Equal("none found", applied.Items.Single(i => i.ItemCode == "VAG99999").Result);
        repo.Verify(r => r.AddOemsAsync(9001, It.Is<IReadOnlyList<string>>(o => o.SequenceEqual(new[] { "5Q0411303P", "5Q0411303L", "5Q0411303S" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
