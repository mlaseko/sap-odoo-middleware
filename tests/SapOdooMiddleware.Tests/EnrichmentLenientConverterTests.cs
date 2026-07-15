using System.Text.Json;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Enrichment DTO must tolerate DGX shape drift on optional fields: a URL that arrives as a one-element
/// array, or categories that arrive as bare strings vs objects, must never fail the whole line's parse.
/// </summary>
public class EnrichmentLenientConverterTests
{
    private static EnrichmentItemData Parse(string json) =>
        JsonSerializer.Deserialize<EnrichmentItemData>(json)!;

    [Fact]
    public void StringField_PlainString_Unchanged()
    {
        var d = Parse("""{ "image_url": "http://x/a.jpg", "all_image_urls": "http://x/a.jpg" }""");
        Assert.Equal("http://x/a.jpg", d.ImageUrl);
        Assert.Equal("http://x/a.jpg", d.AllImageUrls);
    }

    [Fact]
    public void StringField_Array_JoinedNotThrown()
    {
        var d = Parse("""{ "image_url": ["http://x/a.jpg", "http://x/b.jpg"], "all_image_urls": ["http://x/a.jpg"] }""");
        Assert.Equal("http://x/a.jpg,http://x/b.jpg", d.ImageUrl);
        Assert.Equal("http://x/a.jpg", d.AllImageUrls);
    }

    [Fact]
    public void StringField_Object_TreatedAsNull()
    {
        var d = Parse("""{ "product_url": { "href": "http://x" } }""");
        Assert.Null(d.ProductUrl);
    }

    [Fact]
    public void Categories_StringArray_KeptAsIs()
    {
        var d = Parse("""{ "tecdoc_categories": ["Land Rover", "Brakes"] }""");
        Assert.Equal(new[] { "Land Rover", "Brakes" }, d.TecdocCategories);
    }

    [Fact]
    public void Categories_ObjectArray_NamesExtracted()
    {
        var d = Parse("""{ "tecdoc_categories": [ { "id": 1, "name": "Brakes" }, { "label": "Filters" } ] }""");
        Assert.Equal(new[] { "Brakes", "Filters" }, d.TecdocCategories);
    }

    [Fact]
    public void Categories_BareString_WrappedInList()
    {
        var d = Parse("""{ "tecdoc_categories": "Land Rover" }""");
        Assert.Equal(new[] { "Land Rover" }, d.TecdocCategories);
    }

    [Fact]
    public void RoundTrips_BackToScalarAndArray()
    {
        var d = Parse("""{ "image_url": ["http://x/a.jpg"], "tecdoc_categories": [ { "name": "Brakes" } ] }""");
        var json = JsonSerializer.Serialize(d);
        // Re-serialization normalizes to a plain string + string array (what EnrichmentResultRouter stores).
        Assert.Contains("\"image_url\":\"http://x/a.jpg\"", json);
        Assert.Contains("\"tecdoc_categories\":[\"Brakes\"]", json);
    }
}
