using System.Text.Json;
using System.Text.Json.Serialization;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// The DGX manufacturer_resolution block must (a) bind to the typed property and (b) survive the
/// deserialize → re-serialize round-trip EnrichmentResultRouter performs when it stores the enrichment on
/// the line — so Part 2's review UI can read the operator candidates. Rendering contract: candidates keep
/// their order, and a set-derived candidate with share 0.0 is a real option (not dropped).
/// </summary>
public class ManufacturerResolutionTests
{
    // Mirrors HttpEnrichmentClient's read options; the router re-serializes with default options.
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private const string DgxJson = """
    {
      "status": "partial",
      "item_data": { "primary_description": "Water Pump" },
      "manufacturer_resolution": {
        "resolved": false,
        "candidates": [
          { "code": "VAG", "label": "VW/Audi/Porsche", "share": 0.0, "evidence": "brand 'vika' makes VAG parts (dominant)" },
          { "code": "BM",  "label": "BMW",             "share": 0.0, "evidence": "brand 'vika' makes BM parts" },
          { "code": "MB",  "label": "Mercedes-Benz",   "share": 0.0, "evidence": "brand 'vika' makes MB parts" }
        ]
      }
    }
    """;

    [Fact]
    public void ManufacturerResolution_BindsToTypedProperty()
    {
        var enr = JsonSerializer.Deserialize<EnrichmentResponse>(DgxJson, ReadOpts)!;

        Assert.NotNull(enr.ManufacturerResolution);
        Assert.False(enr.ManufacturerResolution!.Resolved);
        var c = enr.ManufacturerResolution.Candidates!;
        Assert.Equal(3, c.Count);
        // Order preserved (order IS the ranking); VAG first.
        Assert.Equal(new[] { "VAG", "BM", "MB" }, c.Select(x => x.Code));
        // A set-derived 0.0-share candidate is still a real option carrying evidence text.
        Assert.Equal(0.0m, c[0].Share);
        Assert.Equal("brand 'vika' makes VAG parts (dominant)", c[0].Evidence);
    }

    [Fact]
    public void ManufacturerResolution_SurvivesReserialization()
    {
        // Deserialize (client) → serialize (router stores EnrichmentPayloadJson) → deserialize (provisioning).
        var enr = JsonSerializer.Deserialize<EnrichmentResponse>(DgxJson, ReadOpts)!;
        var stored = JsonSerializer.Serialize(enr);
        var round = JsonSerializer.Deserialize<EnrichmentResponse>(stored, ReadOpts)!;

        Assert.NotNull(round.ManufacturerResolution);
        Assert.Equal(3, round.ManufacturerResolution!.Candidates!.Count);
        Assert.Equal(new[] { "VAG", "BM", "MB" }, round.ManufacturerResolution.Candidates!.Select(x => x.Code));
        // The typed property owns the field now — it must not ALSO leak into the extension-data bag.
        Assert.False(round.Extra?.ContainsKey("manufacturer_resolution") ?? false);
    }
}
