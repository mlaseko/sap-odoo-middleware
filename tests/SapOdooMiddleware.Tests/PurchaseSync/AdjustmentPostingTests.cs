using SapOdooMiddleware.Models.PurchaseSync;
using SapOdooMiddleware.Services.PurchaseSync;

namespace SapOdooMiddleware.Tests.PurchaseSync;

public class AdjustmentPostingTests
{
    private static SupplierAdjustmentRequest Request(string direction, double amount = 25.0, string currency = "EUR", double rate = 2950.0) =>
        new()
        {
            OdooRef = "SAE-7", CardCode = "S00001", DocDate = new DateTime(2026, 11, 3), Currency = currency,
            Rate = rate, Amount = amount, Direction = direction, Reason = "bank_charges", SapGlCode = "6011510",
        };

    [Fact]
    public void CreditSupplier_ForeignCurrency_CreditsBpAndDebitsGlInBothCurrencies()
    {
        var lines = AdjustmentPosting.Compute(Request(AdjustmentPosting.CreditSupplier), "TZS");

        Assert.True(lines.IsForeign);
        Assert.Equal(0, lines.BusinessPartner.DebitLc);
        Assert.Equal(73750.0, lines.BusinessPartner.CreditLc);
        Assert.Equal(25.0, lines.BusinessPartner.CreditFc);
        Assert.Equal(0, lines.BusinessPartner.DebitFc);

        Assert.Equal(73750.0, lines.GlAccount.DebitLc);
        Assert.Equal(0, lines.GlAccount.CreditLc);
        Assert.Equal(25.0, lines.GlAccount.DebitFc);
        Assert.Equal(0, lines.GlAccount.CreditFc);
    }

    [Fact]
    public void DebitSupplier_IsTheMirror()
    {
        var lines = AdjustmentPosting.Compute(Request(AdjustmentPosting.DebitSupplier, amount: 3.0), "TZS");

        Assert.Equal(8850.0, lines.BusinessPartner.DebitLc);
        Assert.Equal(3.0, lines.BusinessPartner.DebitFc);
        Assert.Equal(0, lines.BusinessPartner.CreditLc);
        Assert.Equal(8850.0, lines.GlAccount.CreditLc);
        Assert.Equal(3.0, lines.GlAccount.CreditFc);
    }

    [Fact]
    public void LocalCurrency_UsesTheAmountAsLcAndNoFc()
    {
        var lines = AdjustmentPosting.Compute(Request(AdjustmentPosting.CreditSupplier, amount: 5000.0, currency: "tzs", rate: 0), "TZS");

        Assert.False(lines.IsForeign);
        Assert.Equal(5000.0, lines.BusinessPartner.CreditLc);
        Assert.Equal(0, lines.BusinessPartner.CreditFc);
        Assert.Equal(5000.0, lines.GlAccount.DebitLc);
    }

    [Fact]
    public void LcAmountIsRoundedToCents()
    {
        var lines = AdjustmentPosting.Compute(Request(AdjustmentPosting.CreditSupplier, amount: 0.1, rate: 2949.999), "TZS");
        Assert.Equal(295.0, lines.BusinessPartner.CreditLc);
    }

    [Theory]
    [InlineData("credit", 25.0, 2950.0)]
    [InlineData("", 25.0, 2950.0)]
    [InlineData("credit_supplier", 0.0, 2950.0)]
    [InlineData("credit_supplier", -1.0, 2950.0)]
    [InlineData("credit_supplier", 25.0, 0.0)]
    public void InvalidInput_Throws(string direction, double amount, double rate)
    {
        Assert.Throws<ArgumentException>(() => AdjustmentPosting.Compute(Request(direction, amount, "EUR", rate), "TZS"));
    }

    [Fact]
    public void IsValidDirection_AcceptsOnlyTheTwoValues()
    {
        Assert.True(AdjustmentPosting.IsValidDirection("credit_supplier"));
        Assert.True(AdjustmentPosting.IsValidDirection("debit_supplier"));
        Assert.False(AdjustmentPosting.IsValidDirection("Credit_Supplier"));
        Assert.False(AdjustmentPosting.IsValidDirection(null));
    }
}
