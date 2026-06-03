using System.Text.Json;
using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Services.Autohub;

// ---- DGX /enrich_item contract (§5.1) ----

public sealed record EnrichmentRequest(
    [property: JsonPropertyName("supplier_article_number")] string? SupplierArticleNumber,
    [property: JsonPropertyName("oem_numbers")]             IReadOnlyList<string> OemNumbers,
    [property: JsonPropertyName("brand")]                  string? Brand,
    [property: JsonPropertyName("description")]            string? Description,
    [property: JsonPropertyName("vehicle_category_hint")]  string? VehicleCategoryHint);

public sealed record EnrichmentResponse
{
    [JsonPropertyName("enrichment_source")]     public string?            EnrichmentSource    { get; init; }
    [JsonPropertyName("borrowed_from")]         public BorrowedFrom?      BorrowedFrom        { get; init; }
    [JsonPropertyName("confirmation_required")] public bool               ConfirmationRequired{ get; init; }
    [JsonPropertyName("item_data")]             public EnrichmentItemData? ItemData           { get; init; }
    [JsonPropertyName("noise_filtered_tokens")] public List<string>?      NoiseFilteredTokens { get; init; }
}

public sealed record BorrowedFrom
{
    [JsonPropertyName("article_number")]  public string?  ArticleNumber   { get; init; }
    [JsonPropertyName("supplier_id")]     public string?  SupplierId      { get; init; }
    [JsonPropertyName("supplier_name")]   public string?  SupplierName    { get; init; }
    [JsonPropertyName("match_via_oem")]   public string?  MatchViaOem     { get; init; }
    [JsonPropertyName("match_confidence")]public decimal? MatchConfidence { get; init; }
}

public sealed record EnrichmentItemData
{
    [JsonPropertyName("primary_description")]   public string?       PrimaryDescription { get; init; }
    [JsonPropertyName("frgn_name")]             public string?       FrgnName           { get; init; }
    [JsonPropertyName("fit_for_auto")]          public string?       FitForAuto         { get; init; }
    [JsonPropertyName("image_url")]             public string?       ImageUrl           { get; init; }
    [JsonPropertyName("all_image_urls")]        public string?       AllImageUrls       { get; init; }
    [JsonPropertyName("product_url")]           public string?       ProductUrl         { get; init; }
    [JsonPropertyName("tecdoc_categories")]     public List<string>? TecdocCategories   { get; init; }
    [JsonPropertyName("compatible_vehicles")]   public List<JsonElement>? CompatibleVehicles { get; init; }
    [JsonPropertyName("filtered_oems")]         public List<string>? FilteredOems       { get; init; }
    [JsonPropertyName("suggested_itms_grp_cod")]public int?          SuggestedItmsGrpCod{ get; init; }
    [JsonPropertyName("suggested_sku_prefix")]  public string?       SuggestedSkuPrefix { get; init; }
}

/// <summary>Tenant-neutral input to enrichment (decoupled from the staging row shape).</summary>
public sealed record EnrichmentInput(
    string? SupplierArticleNumber,
    IReadOnlyList<string> OemNumbers,
    string? Brand,
    string? Description,
    string? VehicleCategoryHint);

public interface IEnrichmentService
{
    Task<EnrichmentResponse> EnrichLineAsync(EnrichmentInput input, CancellationToken ct);
}

/// <summary>
/// Orchestrates enrichment for one line: runs the Option-C OEM filter (§6.3) to strip
/// position/engine noise, then calls the DGX <c>/enrich_item</c> endpoint, which internally does the
/// Germax-first (Option 1) and TecDoc/borrowed lookups and returns a complete package. Persistence
/// and the operator-confirmation gate live in the orchestration/API layer (slice 3).
/// </summary>
public sealed class EnrichmentService : IEnrichmentService
{
    private readonly IEnrichmentClient _client;
    private readonly IOemFilterService _filter;

    public EnrichmentService(IEnrichmentClient client, IOemFilterService filter)
    {
        _client = client;
        _filter = filter;
    }

    public Task<EnrichmentResponse> EnrichLineAsync(EnrichmentInput input, CancellationToken ct)
    {
        var clean = _filter.Filter(input.OemNumbers, input.SupplierArticleNumber, input.Brand).CleanOems;
        var req = new EnrichmentRequest(
            input.SupplierArticleNumber, clean, input.Brand, input.Description, input.VehicleCategoryHint);
        return _client.EnrichAsync(req, ct);
    }
}
