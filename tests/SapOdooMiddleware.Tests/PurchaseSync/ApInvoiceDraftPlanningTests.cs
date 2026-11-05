using SapOdooMiddleware.Models.PurchaseSync;
using SapOdooMiddleware.Services.PurchaseSync;
using Xunit;

namespace SapOdooMiddleware.Tests.PurchaseSync;

public class ApInvoiceDraftPlanningTests
{
    private static ApInvoiceDraftRequest Request(double total, double freight = 0, params ApInvoiceDraftLineRequest[] lines) => new()
    {
        OdooShipmentId = "A12423210", CardCode = "S00001", InvoiceNumber = "VR000531623",
        InvoiceDate = new DateTime(2026, 8, 25), Currency = "EUR", GrpoDocentry = 61,
        FreightFc = freight, FreightExpenseCode = 1, MarketingExpenseCode = 2, InvoiceTotalFc = total, Lines = lines.ToList(),
    };

    private static ApInvoiceDraftLineRequest Inv(string code, double qty, double price, double disc = 0, bool free = false) => new()
    {
        ItemCode = code, Quantity = qty, UnitPrice = price, DiscountPercent = disc, IsFree = free,
        Amount = Math.Round(qty * price * (1 - disc / 100), 2),
    };

    private static ApGrpoLine Grpo(int line, string code, double qty, double price = 1, double disc = 0) =>
        new(line, code, qty, qty, price, disc, "O");

    [Fact]
    public void Pilot_A12423210_two_lines_and_freight_equal_the_invoice()
    {
        // VR000531623: 3,888 × 5.51 + 1,600 × 30.68 + freight 3,005 = 73,515.88 EUR
        var req = Request(73515.88, 3005, Inv("2315", 3888, 5.51), Inv("8973", 1600, 30.68));
        var plan = ApInvoiceDraftPlanning.Build(req, new[] { Grpo(0, "2315", 3888, 5.51), Grpo(1, "8973", 1600, 30.68) });

        Assert.Equal(2, plan.ItemLines.Count);
        Assert.Equal(70510.88m, plan.ItemTotal);
        Assert.Equal(3005m, plan.Freight);
        Assert.Equal(73515.88m, plan.ExpectedTotal);
        Assert.Equal(0m, plan.MarketingTotal);
        Assert.Equal(5.51, plan.ItemLines[0].UnitPrice);
    }

    [Fact]
    public void Free_and_charged_lines_of_one_item_pair_by_class()
    {
        // GRPO 56 style: 5156 received twice, once charged, once as a bonus at 100% discount
        var req = Request(156 * 4.68 + 50 * 4.68, 0, Inv("5156", 156, 4.68), Inv("5156", 156, 4.68, 100, true), Inv("5156", 50, 4.68));
        var plan = ApInvoiceDraftPlanning.Build(req, new[]
        {
            Grpo(0, "5156", 156, 4.68), Grpo(1, "5156", 156, 0, 100), Grpo(2, "5156", 50, 4.68),
        });

        Assert.Equal(4.68, plan.ItemLines.Single(l => l.BaseLine == 0).UnitPrice);
        var free = plan.ItemLines.Single(l => l.BaseLine == 1);
        Assert.Equal(100, free.DiscountPercent);
        Assert.Equal(0m, free.LineTotal);
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void A_charged_line_received_at_zero_is_repriced_and_noted()
    {
        // GRPO 54 style: 25077 received at price 0 but charged 13.12 on the invoice
        var req = Request(40 * 13.12, 0, Inv("25077", 40, 13.12));
        var plan = ApInvoiceDraftPlanning.Build(req, new[] { Grpo(0, "25077", 40, 0) });

        Assert.Equal(13.12, plan.ItemLines[0].UnitPrice);
        Assert.Contains(plan.Notes, n => n.Contains("received at zero"));
    }

    [Fact]
    public void Display_items_not_in_stock_become_the_marketing_expense_and_free_promo_is_noted()
    {
        // VR000315311 style: a rack charged 36.00, a free rack, stock item 2326
        var req = Request(200 * 20.19 + 36, 0,
            Inv("2326", 200, 20.19), Inv("9985", 1, 36), Inv("9988", 1, 205.3, 100, true));
        var plan = ApInvoiceDraftPlanning.Build(req, new[] { Grpo(0, "2326", 200, 20.19) });

        Assert.Equal(36m, plan.MarketingTotal);
        Assert.Single(plan.ItemLines);
        Assert.Contains(plan.Notes, n => n.Contains("1 free invoice line"));
    }

    [Fact]
    public void A_total_difference_is_refused()
    {
        var req = Request(73600, 3005, Inv("2315", 3888, 5.51), Inv("8973", 1600, 30.68));
        var ex = Assert.Throws<ApInvoiceGuardException>(() =>
            ApInvoiceDraftPlanning.Build(req, new[] { Grpo(0, "2315", 3888), Grpo(1, "8973", 1600) }));
        Assert.Equal("TOTAL_MISMATCH", ex.Code);
    }

    [Fact]
    public void A_quantity_difference_names_the_item()
    {
        var req = Request(100 * 5.51, 0, Inv("2315", 100, 5.51));
        var ex = Assert.Throws<ApInvoiceGuardException>(() =>
            ApInvoiceDraftPlanning.Build(req, new[] { Grpo(0, "2315", 98) }));
        Assert.Equal("LINES_DO_NOT_MATCH", ex.Code);
        Assert.Contains("received 98", ex.Message);
    }

    [Fact]
    public void A_GRPO_line_already_invoiced_is_refused()
    {
        var req = Request(100 * 5.51, 0, Inv("2315", 100, 5.51));
        var ex = Assert.Throws<ApInvoiceGuardException>(() =>
            ApInvoiceDraftPlanning.Build(req, new[] { new ApGrpoLine(0, "2315", 100, 0, 5.51, 0, "C") }));
        Assert.Equal("GRPO_ALREADY_INVOICED", ex.Code);
    }

    [Fact]
    public void Marketing_without_its_expense_code_is_refused()
    {
        var req = Request(100 * 5.51 + 36, 0, Inv("2315", 100, 5.51), Inv("9985", 1, 36));
        req.MarketingExpenseCode = null;
        var ex = Assert.Throws<ApInvoiceGuardException>(() =>
            ApInvoiceDraftPlanning.Build(req, new[] { Grpo(0, "2315", 100) }));
        Assert.Equal("MARKETING_CODE_MISSING", ex.Code);
    }

    [Fact]
    public void Validate_requires_the_freight_code_when_there_is_freight()
    {
        var req = Request(100, 10, Inv("2315", 1, 90));
        req.FreightExpenseCode = null;
        Assert.Contains(ApInvoiceDraftPlanning.Validate(req), e => e.Contains("freight_expense_code"));
        Assert.Empty(ApInvoiceDraftPlanning.Validate(Request(100, 0, Inv("2315", 1, 100))));
    }
}
