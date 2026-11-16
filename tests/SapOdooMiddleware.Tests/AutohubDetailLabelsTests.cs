using SapOdooMiddleware.Areas.Autohub.Pages.Documents;

namespace SapOdooMiddleware.Tests;

/// <summary>Plain-language tooltips on the Autohub invoice page (Phase 0 UI clean-up).</summary>
public class AutohubDetailLabelsTests
{
    [Fact]
    public void Review_reason_explains_the_verdict_and_names_the_matched_part()
    {
        var uncertain = DetailModel.ReviewReason("UNCERTAIN", "Generator", "BOSCH");
        Assert.Contains("could not be confirmed as the same kind of part", uncertain);
        Assert.Contains("Generator (BOSCH)", uncertain);

        var different = DetailModel.ReviewReason("different", null, null);
        Assert.Contains("different kind of part", different);
        Assert.DoesNotContain("Matched part", different);
    }

    [Theory]
    [InlineData("rapidapi_tecdoc_live_create_new", "live TecDoc search")]
    [InlineData("germax_local_create_new", "Germax catalogue")]
    [InlineData("tier1_oem", "Already in SAP")]
    [InlineData("tecdoc_direct_cross_supplier_create_new", "another supplier's part")]
    [InlineData("enrichment_direct", "this supplier's article number")]
    public void Source_explanation_is_plain_language(string strategy, string expected)
    {
        Assert.Contains(expected, DetailModel.SourceExplanation(strategy));
    }
}
