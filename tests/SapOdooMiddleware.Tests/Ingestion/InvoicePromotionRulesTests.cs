using SapOdooMiddleware.Ingestion;
using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Tests.Ingestion;

public class InvoicePromotionRulesTests
{
    private static InvoiceLine Line(
        decimal? discountPct = 0m, decimal? unitPrice = 10m, decimal? quantity = 1m,
        decimal? lineTotal = 10m, string? packSize = "20l", string? unit = "l")
        => new()
        {
            DiscountPct = discountPct,
            UnitPrice = unitPrice,
            Quantity = quantity,
            LineTotal = lineTotal,
            PackSize = packSize,
            Unit = unit
        };

    [Fact]
    public void NormalLine_IsNotPromotional()
        => Assert.False(InvoicePromotionRules.IsPromotional(Line()));

    [Fact]
    public void HundredPercentDiscount_IsPromotional()
        => Assert.True(InvoicePromotionRules.IsPromotional(Line(discountPct: 100m)));

    [Fact]
    public void OverHundredPercentDiscount_IsPromotional()
        => Assert.True(InvoicePromotionRules.IsPromotional(Line(discountPct: 110m)));

    [Fact]
    public void ZeroPriceWithQuantity_IsPromotional()
        => Assert.True(InvoicePromotionRules.IsPromotional(Line(unitPrice: 0m, lineTotal: 0m, quantity: 5m)));

    [Fact]
    public void NullPriceWithQuantity_IsPromotional()
        => Assert.True(InvoicePromotionRules.IsPromotional(Line(unitPrice: null, lineTotal: null, quantity: 5m)));

    [Fact]
    public void ZeroPriceButZeroQuantity_IsNotPromotional()
        => Assert.False(InvoicePromotionRules.IsPromotional(Line(unitPrice: 0m, lineTotal: 0m, quantity: 0m)));

    [Theory]
    [InlineData("1 Stk")]
    [InlineData("1 stk")]
    [InlineData(" 1 Stk ")]
    public void PackSizeOneStk_IsPromotional(string packSize)
        => Assert.True(InvoicePromotionRules.IsPromotional(Line(packSize: packSize)));

    [Theory]
    [InlineData("Stk")]
    [InlineData("stk")]
    [InlineData(" Stk ")]
    public void UnitStk_IsPromotional(string unit)
        => Assert.True(InvoicePromotionRules.IsPromotional(Line(unit: unit)));

    [Fact]
    public void RegularPackAndUnit_IsNotPromotional()
        => Assert.False(InvoicePromotionRules.IsPromotional(Line(packSize: "20l", unit: "l")));
}
