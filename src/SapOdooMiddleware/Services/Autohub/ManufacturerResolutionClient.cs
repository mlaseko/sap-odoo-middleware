using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.Autohub;

// ---- DGX /resolve_manufacturer contract (manufacturer-resolution Part 2) ----

/// <summary>
/// Operator's marque choice for a held line. Identity for the stored ruling is
/// <c>(supplier_article_number, lower(brand))</c> — <c>brand</c> MUST be the staging line's Brand
/// ("vika", "Borsehung"), the same value the S1 lookup uses at enrichment time; sending anything else
/// (e.g. the TecDoc manufacturer, which this system confusingly calls "supplier_name") files the ruling
/// under a mismatched key and identical parts re-queue next month. <c>oem_numbers</c> is what DGX
/// re-ranks (v1 re-ranks what the caller sends; it does not fetch stored cross-refs) — pass the line's
/// OEMs to get them back marque-ordered in <c>ranked_oems</c>, or omit for a valid resolution with an
/// empty ranking. <c>decided_by</c> is the audit identity (defaults "operator" server-side).
/// </summary>
public sealed record ResolveManufacturerRequest(
    [property: JsonPropertyName("supplier_article_number")] string? SupplierArticleNumber,
    [property: JsonPropertyName("brand")]                   string? Brand,
    [property: JsonPropertyName("manufacturer")]            string  Manufacturer,
    [property: JsonPropertyName("decided_by")]              string? DecidedBy,
    [property: JsonPropertyName("oem_numbers")]             IReadOnlyList<string>? OemNumbers);

/// <summary>
/// The marque package DGX returns for a resolved line (v1 shape). The name/enrichment payload were
/// already delivered by the original <c>/enrich_item</c> response the middleware holds on the line, so
/// this carries only what the marque decision produces — the middleware merges it into the held payload.
/// </summary>
public sealed record ManufacturerPackage
{
    [JsonPropertyName("prefix")]                 public string?       Prefix              { get; init; }
    [JsonPropertyName("suggested_itms_grp_cod")] public int?          SuggestedItmsGrpCod { get; init; }
    [JsonPropertyName("vehicle_category")]       public string?       VehicleCategory     { get; init; }
    [JsonPropertyName("ranked_oems")]            public List<string>? RankedOems          { get; init; }
    [JsonPropertyName("ruling_stored")]          public bool          RulingStored        { get; init; }
    [JsonPropertyName("error")]                  public string?       Error               { get; init; }
}

public interface IManufacturerResolutionClient
{
    /// <summary>Finalize a held line under the operator's chosen marque (DGX <c>/resolve_manufacturer</c>). Idempotent.</summary>
    Task<ManufacturerPackage> ResolveAsync(ResolveManufacturerRequest request, CancellationToken ct);
}

/// <summary>
/// Typed HttpClient over the DGX <c>/resolve_manufacturer</c> endpoint. Base URL resolves from the active
/// tenant (Companies:Autohub:Classifier:BaseUrl) via <see cref="ICompanyContext"/>, same as the enrichment
/// and donor-search clients. Non-2xx / empty responses come back as a package with <see cref="ManufacturerPackage.Error"/>
/// set (never throws for a normal error), so the caller can surface it without a 500.
/// </summary>
public sealed class ManufacturerResolutionClient : IManufacturerResolutionClient
{
    private readonly HttpClient _http;
    private readonly ICompanyContext _company;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public ManufacturerResolutionClient(HttpClient http, ICompanyContext company)
    {
        _http = http;
        _company = company;
    }

    private string Base => _company.Current.Classifier.BaseUrl.TrimEnd('/');

    public async Task<ManufacturerPackage> ResolveAsync(ResolveManufacturerRequest request, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync($"{Base}/resolve_manufacturer", request, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return new ManufacturerPackage { Error = ExtractError(text) ?? $"HTTP {(int)resp.StatusCode}" };

        return (string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<ManufacturerPackage>(text, Json))
               ?? new ManufacturerPackage { Error = "Empty response from /resolve_manufacturer." };
    }

    private static string? ExtractError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// Folds a resolved marque package into the held enrichment payload. Pure/testable, no I/O.
/// </summary>
public static class ManufacturerResolutionMerge
{
    /// <summary>
    /// Set the resolved SKU prefix (the marque code) and, when the package supplies one, the SAP item
    /// group on the held enrichment, and mark the <c>manufacturer_resolution</c> block resolved (keeping
    /// the candidate evidence for audit). The OEM-chain re-ranking is a DGX-side Neon effect the middleware
    /// reads at create time, so <c>ranked_oems</c> is informational and not merged into the name here.
    /// Returns a new <see cref="EnrichmentResponse"/>; the input is unchanged.
    /// </summary>
    public static EnrichmentResponse Apply(EnrichmentResponse held, ManufacturerPackage pkg)
    {
        var item = held.ItemData ?? new EnrichmentItemData();
        var mergedItem = item with
        {
            SuggestedSkuPrefix  = pkg.Prefix,
            SuggestedItmsGrpCod = pkg.SuggestedItmsGrpCod ?? item.SuggestedItmsGrpCod,
        };
        // Rewrite the block to its resolved shape (method=operator + prefix); reason/candidates are
        // unresolved-only, so they're cleared now that a human decided.
        return held with
        {
            ItemData = mergedItem,
            ManufacturerResolution = new ManufacturerResolution
            {
                Resolved = true,
                Method   = "operator",
                Prefix   = pkg.Prefix,
            },
        };
    }
}
