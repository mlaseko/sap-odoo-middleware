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

    // ---- ManufacturerResolutionMerge (folding the resolved marque package into the held enrichment) ----

    [Fact]
    public void Merge_AppliesPrefixAndGroup_MarksResolved_KeepsCandidatesAndOtherFields()
    {
        var held = JsonSerializer.Deserialize<EnrichmentResponse>(DgxJson, ReadOpts)!;
        var pkg = new ManufacturerPackage { Prefix = "MB", SuggestedItmsGrpCod = 137, RulingStored = true };

        var merged = ManufacturerResolutionMerge.Apply(held, pkg);

        Assert.Equal("MB", merged.ItemData!.SuggestedSkuPrefix);
        Assert.Equal(137, merged.ItemData.SuggestedItmsGrpCod);
        Assert.Equal("Water Pump", merged.ItemData.PrimaryDescription);   // untouched item_data survives the `with`
        Assert.True(merged.ManufacturerResolution!.Resolved);
        Assert.Equal(3, merged.ManufacturerResolution.Candidates!.Count);  // evidence kept for audit
        // The input is unchanged (Apply returns a new record).
        Assert.Null(held.ItemData!.SuggestedSkuPrefix);
    }

    [Fact]
    public void Merge_KeepsExistingGroup_WhenPackageOmitsIt()
    {
        var held = JsonSerializer.Deserialize<EnrichmentResponse>(
            """{ "item_data": { "suggested_itms_grp_cod": 140 } }""", ReadOpts)!;
        var pkg = new ManufacturerPackage { Prefix = "VAG", SuggestedItmsGrpCod = null };

        var merged = ManufacturerResolutionMerge.Apply(held, pkg);

        Assert.Equal("VAG", merged.ItemData!.SuggestedSkuPrefix);
        Assert.Equal(140, merged.ItemData.SuggestedItmsGrpCod);   // package omitted the group → keep existing
    }

    [Fact]
    public void Merge_ResolvedPrefix_PassesTheNoMachineGenGuard()
    {
        // A resolved line must mint on the next bulk-create — i.e. its prefix is no longer "unresolved".
        var held = JsonSerializer.Deserialize<EnrichmentResponse>(DgxJson, ReadOpts)!;
        var merged = ManufacturerResolutionMerge.Apply(held, new ManufacturerPackage { Prefix = "BM" });
        Assert.False(PartsItemProvisioningService.IsUnresolvedPrefix(merged.ItemData!.SuggestedSkuPrefix));
    }
}
