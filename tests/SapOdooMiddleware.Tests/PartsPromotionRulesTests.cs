using SapOdooMiddleware.Ingestion;
using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Tests;

public class PartsPromotionRulesTests
{
    [Fact]
    public void NormalPricedLine_IsNotPromotional()
    {
        var line = new PartsInvoiceLine { Quantity = 1, UnitPriceForeign = 10m, LineTotalForeign = 10m, DiscountPct = 0m };
        Assert.False(PartsPromotionRules.IsPromotional(line));
    }

    [Fact]
    public void FullDiscount_IsPromotional()
    {
        var line = new PartsInvoiceLine { Quantity = 1, UnitPriceForeign = 10m, LineTotalForeign = 0m, DiscountPct = 100m };
        Assert.True(PartsPromotionRules.IsPromotional(line));
    }

    [Fact]
    public void ZeroUnitPriceWithQuantity_IsPromotional()
    {
        var line = new PartsInvoiceLine { Quantity = 2, UnitPriceForeign = 0m, LineTotalForeign = 0m };
        Assert.True(PartsPromotionRules.IsPromotional(line));
    }

    [Fact]
    public void ZeroLineTotalWithQuantity_IsPromotional()
    {
        var line = new PartsInvoiceLine { Quantity = 1, UnitPriceForeign = 5m, LineTotalForeign = 0m };
        Assert.True(PartsPromotionRules.IsPromotional(line));
    }

    [Fact]
    public void ZeroQuantity_IsNotPromotional()
    {
        // No real ordered quantity → not a free-goods promo even though price is zero.
        var line = new PartsInvoiceLine { Quantity = 0, UnitPriceForeign = 0m, LineTotalForeign = 0m };
        Assert.False(PartsPromotionRules.IsPromotional(line));
    }
}
