using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.Autohub;

// ---- DGX "find more candidates" contract (broad TecDoc/RapidAPI pool + lazy OEMs + materialize) ----

/// <summary>One candidate from the broad DGX <c>/candidates_for_line</c> pool (RapidAPI/TecDoc scored).</summary>
public sealed record ApiDonorCandidate
{
    [JsonPropertyName("name")]              public string?  Name             { get; init; }
    [JsonPropertyName("supplier")]          public string?  Supplier         { get; init; }
    [JsonPropertyName("article_number")]    public string?  ArticleNumber    { get; init; }
    [JsonPropertyName("tecdoc_article_id")] public long?    TecdocArticleId  { get; init; }
    [JsonPropertyName("supplier_id")]       public long?    SupplierId       { get; init; }
    [JsonPropertyName("verdict")]           public string?  Verdict          { get; init; }
    [JsonPropertyName("score")]             public decimal? Score            { get; init; }
    [JsonPropertyName("auto_pick_eligible")] public bool?   AutoPickEligible { get; init; }
    /// <summary>The ONE OEM that connected this article to the line (shown free, without a lazy fetch).</summary>
    [JsonPropertyName("matched_oem")]       public string?  MatchedOem       { get; init; }
    [JsonPropertyName("shared_oem_count")]  public int?     SharedOemCount   { get; init; }
    [JsonPropertyName("crossref_count")]    public long?    CrossrefCount    { get; init; }
    [JsonPropertyName("image_url")]         public string?  ImageUrl         { get; init; }
    [JsonPropertyName("is_default")]        public bool     IsDefault        { get; init; }
    [JsonPropertyName("source")]            public string?  Source           { get; init; }
}

public sealed record DonorSearchResponse
{
    [JsonPropertyName("found")]           public bool                     Found          { get; init; }
    [JsonPropertyName("candidate_count")] public int                     CandidateCount { get; init; }
    [JsonPropertyName("candidates")]      public List<ApiDonorCandidate>? Candidates     { get; init; }
    [JsonPropertyName("error")]           public string?                  Error          { get; init; }
}

public sealed record ArticleOemsResponse
{
    [JsonPropertyName("found")]          public bool          Found         { get; init; }
    [JsonPropertyName("oem_numbers")]    public List<string>? OemNumbers    { get; init; }
    [JsonPropertyName("oem_count")]      public int?          OemCount      { get; init; }
    [JsonPropertyName("crossref_count")] public long?         CrossrefCount { get; init; }
    [JsonPropertyName("error")]          public string?       Error         { get; init; }
}

/// <summary>Ask the DGX to upsert a chosen TecDoc article into <c>oitm</c> (item_code NULL) and return its id.</summary>
public sealed record MaterializeRequest(
    [property: JsonPropertyName("tecdoc_article_id")] long?  TecdocArticleId,
    [property: JsonPropertyName("article_number")]    string? ArticleNumber,
    [property: JsonPropertyName("supplier")]          string? Supplier,
    [property: JsonPropertyName("supplier_id")]       long?  SupplierId,
    [property: JsonPropertyName("description")]       string? Description,
    [property: JsonPropertyName("line_oems")]         IReadOnlyList<string>? LineOems);

public sealed record MaterializeResponse
{
    [JsonPropertyName("found")]                     public bool          Found                   { get; init; }
    /// <summary>The parts_catalog oitm.id the swap re-points to. The only strictly-required field.</summary>
    [JsonPropertyName("neon_oitm_id")]              public long?         NeonOitmId              { get; init; }
    [JsonPropertyName("article_number")]            public string?       ArticleNumber           { get; init; }
    [JsonPropertyName("supplier")]                  public string?       Supplier                { get; init; }
    [JsonPropertyName("name")]                      public string?       Name                    { get; init; }
    [JsonPropertyName("image_url")]                 public string?       ImageUrl                { get; init; }
    [JsonPropertyName("oem_numbers")]               public List<string>? OemNumbers              { get; init; }
    [JsonPropertyName("oem_count")]                 public int?          OemCount                { get; init; }
    [JsonPropertyName("crossref_count")]            public long?         CrossrefCount           { get; init; }
    [JsonPropertyName("part_component")]            public string?       PartComponent           { get; init; }
    [JsonPropertyName("is_kit")]                    public bool          IsKit                   { get; init; }
    [JsonPropertyName("spec_count")]                public int?          SpecCount               { get; init; }
    [JsonPropertyName("compatible_vehicles_count")] public int?          CompatibleVehiclesCount { get; init; }
    [JsonPropertyName("categories_count")]          public int?          CategoriesCount         { get; init; }
    [JsonPropertyName("error")]                     public string?       Error                   { get; init; }
}

/// <summary>Ask the DGX to mint a DEEP own-identity <c>oitm</c> row (identity + name + image + specs +
/// compatible vehicles + categories from the donor's TecDoc record) and return its id. Replaces the
/// middleware's shallow local INSERT so items are complete at creation. Idempotent on
/// (article_number, UPPER(supplier_name)).</summary>
public sealed record MintItemRequest(
    [property: JsonPropertyName("article_number")]         string? ArticleNumber,
    [property: JsonPropertyName("supplier_name")]          string? SupplierName,
    [property: JsonPropertyName("oem_numbers")]            IReadOnlyList<string>? OemNumbers,
    [property: JsonPropertyName("donor_tecdoc_article_id")] long?  DonorTecdocArticleId,
    [property: JsonPropertyName("source")]                 string? Source,
    [property: JsonPropertyName("item_code")]              string? ItemCode,
    [property: JsonPropertyName("request_id")]             string? RequestId,
    /// <summary>Seeds the row's name when there's no donor to take it from — without it the classifier
    /// (which requires name &gt; '') can never categorize the item. Ignored when a donor supplies the name.</summary>
    [property: JsonPropertyName("description")]            string? Description = null);

public sealed record MintItemResponse
{
    /// <summary>The parts_catalog oitm.id of the minted row. The only strictly-required field.</summary>
    [JsonPropertyName("neon_oitm_id")]     public long?   NeonOitmId       { get; init; }
    [JsonPropertyName("deep")]             public bool    Deep             { get; init; }
    [JsonPropertyName("specs_written")]    public int?    SpecsWritten     { get; init; }
    [JsonPropertyName("vehicles_written")] public int?    VehiclesWritten  { get; init; }
    [JsonPropertyName("categories_written")] public int?  CategoriesWritten { get; init; }
    [JsonPropertyName("borrowed_from")]    public long?   BorrowedFrom     { get; init; }
    [JsonPropertyName("error")]            public string? Error            { get; init; }
}

public interface IDonorSearchClient
{
    /// <summary>Broad scored candidate pool for a line's OEMs (DGX <c>/candidates_for_line</c>).</summary>
    Task<DonorSearchResponse> CandidatesForLineAsync(IReadOnlyList<string> oemNumbers, string? description, CancellationToken ct);
    /// <summary>Full OEM list for one API candidate, fetched lazily on expand (DGX <c>/article_oems</c>).</summary>
    Task<ArticleOemsResponse> ArticleOemsAsync(long tecdocArticleId, CancellationToken ct);
    /// <summary>Upsert a chosen API candidate into <c>oitm</c> and return its neon_oitm_id (DGX <c>/materialize_candidate</c>).</summary>
    Task<MaterializeResponse> MaterializeCandidateAsync(MaterializeRequest request, CancellationToken ct);

    /// <summary>Mint a DEEP own-identity <c>oitm</c> row from the donor's TecDoc record (DGX <c>/mint_item</c>).</summary>
    Task<MintItemResponse> MintItemAsync(MintItemRequest request, CancellationToken ct);
}

/// <summary>
/// Typed HttpClient over the DGX "find more candidates" endpoints. Base URL resolves from the active
/// tenant (Companies:Autohub:Classifier:BaseUrl) via <see cref="ICompanyContext"/>, same as the
/// enrichment client. Called only on the operator's explicit action (never on modal open). Tolerates
/// <c>501 rapidapi_disabled</c> by returning a not-found result with the error set, so the UI can fall
/// back to the local list instead of throwing.
/// </summary>
public sealed class DonorSearchClient : IDonorSearchClient
{
    private readonly HttpClient _http;
    private readonly ICompanyContext _company;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public DonorSearchClient(HttpClient http, ICompanyContext company)
    {
        _http = http;
        _company = company;
    }

    private string Base => _company.Current.Classifier.BaseUrl.TrimEnd('/');

    public async Task<DonorSearchResponse> CandidatesForLineAsync(IReadOnlyList<string> oemNumbers, string? description, CancellationToken ct)
    {
        var (ok, body, error) = await PostAsync<DonorSearchResponse>(
            "candidates_for_line", new { oem_numbers = oemNumbers, description }, ct);
        return ok && body is not null ? body : new DonorSearchResponse { Found = false, Error = error };
    }

    public async Task<ArticleOemsResponse> ArticleOemsAsync(long tecdocArticleId, CancellationToken ct)
    {
        var (ok, body, error) = await PostAsync<ArticleOemsResponse>(
            "article_oems", new { tecdoc_article_id = tecdocArticleId }, ct);
        return ok && body is not null ? body : new ArticleOemsResponse { Found = false, Error = error };
    }

    public async Task<MaterializeResponse> MaterializeCandidateAsync(MaterializeRequest request, CancellationToken ct)
    {
        var (ok, body, error) = await PostAsync<MaterializeResponse>("materialize_candidate", request, ct);
        return ok && body is not null ? body : new MaterializeResponse { Found = false, Error = error };
    }

    public async Task<MintItemResponse> MintItemAsync(MintItemRequest request, CancellationToken ct)
    {
        var (ok, body, error) = await PostAsync<MintItemResponse>("mint_item", request, ct);
        return ok && body is not null ? body : new MintItemResponse { NeonOitmId = null, Error = error };
    }

    /// <summary>POST helper: returns the deserialized body, or (false, null, error) for 501/non-2xx/empty.</summary>
    private async Task<(bool ok, TResp? body, string? error)> PostAsync<TResp>(string endpoint, object req, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync($"{Base}/{endpoint}", req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == HttpStatusCode.NotImplemented)      // 501 → extended search unavailable
            return (false, default, ExtractError(text) ?? "rapidapi_disabled");
        if (!resp.IsSuccessStatusCode)
            return (false, default, ExtractError(text) ?? $"HTTP {(int)resp.StatusCode}");

        var body = string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<TResp>(text, Json);
        return (true, body, null);
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
