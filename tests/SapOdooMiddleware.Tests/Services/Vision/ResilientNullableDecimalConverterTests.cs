using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Tests.Services.Vision;

/// <summary>
/// Exercises <see cref="ResilientNullableDecimalConverter"/> through the real JSON
/// deserialization path (the same way HttpInvoiceExtractor uses it), covering JSON numbers,
/// null, US/European/percent/currency strings, and unparseable garbage.
/// </summary>
public class ResilientNullableDecimalConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new ResilientNullableDecimalConverter() }
    };

    private sealed record Box([property: JsonPropertyName("v")] decimal? V);

    private static decimal? Parse(string json) => JsonSerializer.Deserialize<Box>(json, Options)!.V;

    [Fact]
    public void JsonNumber_Parses()
        => Assert.Equal(1068.00m, Parse("""{"v":1068.00}"""));

    [Fact]
    public void JsonNull_ReturnsNull()
        => Assert.Null(Parse("""{"v":null}"""));

    [Theory]
    [InlineData("{\"v\":\"1,068.00\"}", "1068.00")]   // US thousands + decimal
    [InlineData("{\"v\":\"1.068,00\"}", "1068.00")]   // European thousands + decimal
    [InlineData("{\"v\":\"46,657.25\"}", "46657.25")] // US mixed
    [InlineData("{\"v\":\"46.657,25\"}", "46657.25")] // European mixed
    [InlineData("{\"v\":\"100 %\"}", "100")]          // percent with space
    [InlineData("{\"v\":\"100%\"}", "100")]           // percent no space
    [InlineData("{\"v\":\"204\"}", "204")]            // plain integer string
    [InlineData("{\"v\":\"4.04\"}", "4.04")]          // decimal-only dot
    [InlineData("{\"v\":\"\\u20AC1.068,00\"}", "1068.00")] // euro-prefixed currency
    public void Strings_ParseFlexibly(string json, string expected)
        => Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), Parse(json));

    [Theory]
    [InlineData("{\"v\":\"\"}")]    // empty string
    [InlineData("{\"v\":\" \"}")]   // whitespace
    [InlineData("{\"v\":\"n/a\"}")] // garbage
    public void Unparseable_ReturnsNull(string json)
        => Assert.Null(Parse(json));
}
