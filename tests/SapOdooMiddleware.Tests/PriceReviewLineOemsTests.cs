using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>Prices tab: a line's OEM numbers are the invoice's own first (a cell can hold several), then the matched part's, each once.</summary>
public class PriceReviewLineOemsTests
{
    private static PartsReviewLineRow Line(List<string> invoice, IReadOnlyList<string>? part) =>
        new(Id: Guid.NewGuid(), DocumentId: Guid.NewGuid(), LineNumber: 1, PageNumber: 1,
            SupplierArticleNumber: "B13720", OemNumbers: invoice, Description: "Fuel delivery unit", Brand: "Borsehung",
            Quantity: 2m, Unit: "PC", UnitPriceForeign: 80.72m, DiscountPct: 8m, LineTotalForeign: null,
            IsPromotional: false, ReviewStatus: "create_new", MatchedItemCode: null, GeneratedItemCode: null,
            EnrichmentSource: null, BorrowedFromArticle: null, EnrichmentConfirmedAt: null, CreateErrorMessage: null,
            MatchStrategy: null, BorrowedFromSupplier: null, SuggestedDonorItemCode: null, SuggestedDonorOitmId: null,
            SuggestedDonorSupplier: null, NeedsReview: false, SelectedComponentVerdict: null, SelectedName: null,
            SelectedSupplier: null, HasDonorCandidates: false, NeedsMarque: false,
            FitForAuto: "VW GOLF V (1K1) 1.6 2003-2008", PartOems: part);

    [Fact]
    public void Invoice_OEMs_first_then_the_parts_each_once()
    {
        var oems = PriceReviewService.OemsOf(Line(
            new List<string> { "1K0919051BB 1K0919051DB", "1k0 919 051 bb" },
            new[] { "1K0919051DB", "1K0919050J", "12" }));
        Assert.Equal(new[] { "1K0919051BB", "1K0919051DB", "1K0919050J" }, oems);
    }

    [Fact]
    public void No_OEMs_gives_an_empty_list() =>
        Assert.Empty(PriceReviewService.OemsOf(Line(new List<string>(), null)));
}
