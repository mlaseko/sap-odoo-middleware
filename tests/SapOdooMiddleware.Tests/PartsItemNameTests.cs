using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// SAP ItemName composition: OEM chain + primary article, then alternate Germax article numbers appended
/// only while they fit under the 200-char cap (the OEM chain + primary are always kept).
/// </summary>
public class PartsItemNameTests
{
    [Fact]
    public void NoAlternates_UnchangedFromOemChainPlusPrimary()
    {
        var name = PartsItemProvisioningService.BuildItemName(new[] { "LR128263", "C2C4198" }, "GL0911");
        Assert.Equal("LR128263/C2C4198/GL0911", name);
    }

    [Fact]
    public void SingleAlternate_AppendedAfterPrimary()
    {
        var name = PartsItemProvisioningService.BuildItemName(
            new[] { "LR128263" }, "GL0911", new[] { "GJ0085" });
        Assert.Equal("LR128263/GL0911/GJ0085", name);
    }

    [Fact]
    public void MultipleAlternates_AllAppendedInOrder()
    {
        var name = PartsItemProvisioningService.BuildItemName(
            new[] { "LR128263" }, "GL0912", new[] { "GJ0750", "GJ0086" });
        Assert.Equal("LR128263/GL0912/GJ0750/GJ0086", name);
    }

    [Fact]
    public void OemChainCapsAtFiveThenPrimary()
    {
        var name = PartsItemProvisioningService.BuildItemName(
            new[] { "A", "B", "C", "D", "E", "F", "G" }, "GL0911");
        Assert.Equal("A/B/C/D/E/GL0911", name);   // only the first five OEMs, then the article
    }

    [Fact]
    public void PrimaryAlwaysKept_AlternateDroppedWhenItWouldExceedCap()
    {
        var oem = new string('X', 190);           // OEM chain + primary already ~196 chars
        var name = PartsItemProvisioningService.BuildItemName(
            new[] { oem }, "GL0911", new[] { "GJ0085" });
        Assert.Equal(oem + "/GL0911", name);       // primary kept, alternate dropped
        Assert.True(name.Length <= 200);
    }

    [Fact]
    public void AppendsOnlyTheAlternatesThatFit()
    {
        var oem = new string('X', 180);            // leaves room for one short alternate but not two
        var name = PartsItemProvisioningService.BuildItemName(
            new[] { oem }, "GL0911", new[] { "GJ0085", "GJ9999" });
        Assert.Equal(oem + "/GL0911/GJ0085", name);
        Assert.True(name.Length <= 200);
    }

    // ---- marque-prefix resolution guard (never mint GEN) ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GEN")]
    [InlineData("gen")]
    [InlineData("  Gen  ")]
    public void UnresolvedPrefix_TreatsBlankAndGenAsUnresolved(string? prefix)
    {
        // The critical guard: a literal "GEN" (what DGX sends while MRES_SHADOW=1) must be treated the same
        // as a missing prefix — held, never minted — not sail past a null-only check.
        Assert.True(PartsItemProvisioningService.IsUnresolvedPrefix(prefix));
    }

    [Theory]
    [InlineData("BM")]
    [InlineData("MB")]
    [InlineData("VAG")]
    [InlineData("GENUINE")]   // a real prefix that merely starts with GEN is NOT the generic marker
    public void UnresolvedPrefix_AllowsRealMarques(string prefix)
    {
        Assert.False(PartsItemProvisioningService.IsUnresolvedPrefix(prefix));
    }
}

/// <summary>Regularized ItemNames: no spaces, each OEM once, invoice OEMs joined with '+' split.</summary>
public class ItemNamesRegularizeTests
{
    [Fact]
    public void SpacedCardOemsAreWrittenWithoutSpacesAndOnlyOnce()
    {
        var oems = ItemNames.MergeOems(new[] { "17517600530" }, new[] { "17 51 7 600 530", "17 21 7 597 891" });
        Assert.Equal("17517600530/17217597891/11075601", ItemNames.Build(oems, "11075601"));
    }

    [Fact]
    public void MercedesSpellingsOfOneNumberAreOneOem()
    {
        var oems = ItemNames.MergeOems(new[] { "A0004212512" }, new[] { "000 421 25 12", "A000 421 25 12" });
        Assert.Equal(new[] { "A0004212512" }, oems);
    }

    [Fact]
    public void LeadingZeroSpellingIsTheSameOem()
    {
        var oems = ItemNames.MergeOems(new[] { "06D109601M" }, new[] { "06D 109 601 M", "6D 109 601 M" });
        Assert.Equal(new[] { "06D109601M" }, oems);
    }

    [Fact]
    public void DashesAndDotsAreKept()
    {
        Assert.Equal("A2053230965-SK/K41797101", ItemNames.Build(new[] { "A2053230965-SK" }, "K41797101"));
        Assert.Equal("0 986.452-041".Replace(" ", ""), ItemNames.Regularize("0 986.452-041"));
    }

    [Fact]
    public void ArticleIsWrittenWithoutSpaces()
    {
        Assert.Equal("LR128263/0986452041", ItemNames.Build(new[] { "LR128263" }, "0 986 452 041"));
    }

    [Fact]
    public void InvoiceOemsJoinedWithPlusAreSplit()
    {
        var clean = new OemFilterService().Filter(new[] { "06M109601G+06M109611S" }, "B13399", "Borsehung").CleanOems;
        Assert.Equal(new[] { "06M109601G", "06M109611S" }, clean);
    }
}
