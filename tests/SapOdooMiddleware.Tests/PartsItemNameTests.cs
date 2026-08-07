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
