using SapOdooMiddleware.Services.PurchaseSync;

namespace SapOdooMiddleware.Tests.PurchaseSync;

public class RefNormalizerTests
{
    [Theory]
    [InlineData(" lm 12345 / 1 ", "LM12345/1")]
    [InlineData("MSKU 123 4567", "MSKU1234567")]
    [InlineData("\tabc\n", "ABC")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_TrimsUppercasesAndRemovesWhitespace(string? input, string expected)
    {
        Assert.Equal(expected, RefNormalizer.Normalize(input));
    }

    [Fact]
    public void Matches_IsCaseAndWhitespaceInsensitive()
    {
        Assert.True(RefNormalizer.Matches("inv 2026-001", "INV2026-001"));
        Assert.True(RefNormalizer.Matches("MSKU1234567", " msku 1234567 "));
    }

    [Fact]
    public void Matches_NeverMatchesEmpty()
    {
        Assert.False(RefNormalizer.Matches("", ""));
        Assert.False(RefNormalizer.Matches(null, null));
        Assert.False(RefNormalizer.Matches("   ", "   "));
        Assert.False(RefNormalizer.Matches("X", null));
    }

    [Theory]
    [InlineData("22", PurchaseEventTypes.PoUpsert)]
    [InlineData("20", PurchaseEventTypes.GrpoLink)]
    [InlineData("18", PurchaseEventTypes.ApInvoiceCreate)]
    [InlineData("19", PurchaseEventTypes.ApCreditCreate)]
    [InlineData("15", null)]
    public void EventTypes_ForAdd_MapsObjectTypes(string objectType, string? expected)
    {
        Assert.Equal(expected, PurchaseEventTypes.ForAdd(objectType));
    }

    [Fact]
    public void EventTypes_All_HasSixDistinctValuesAndExcludesDeliveryRows()
    {
        Assert.Equal(6, PurchaseEventTypes.All.Distinct().Count());
        Assert.True(PurchaseEventTypes.IsPurchaseEvent("AP_CREDIT_CANCEL"));
        Assert.False(PurchaseEventTypes.IsPurchaseEvent("webhook"));
        Assert.False(PurchaseEventTypes.IsPurchaseEvent("resync"));
        Assert.False(PurchaseEventTypes.IsPurchaseEvent(null));
    }
}
