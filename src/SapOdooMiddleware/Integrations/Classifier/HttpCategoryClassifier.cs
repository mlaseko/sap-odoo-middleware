using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Integrations.Classifier;

public record CategoryClassification
{
    public string? ExternalId { get; init; }
    public string? Name { get; init; }
    public double Confidence { get; init; }
    public bool NeedsReview { get; init; }
    public List<string> Candidates { get; init; } = new();
}

public record FamilyClassification
{
    public int? GroupCode { get; init; }
    public string? GroupName { get; init; }
    public double Confidence { get; init; }
    public bool NeedsReview { get; init; }
}

public interface ICategoryClassifier
{
    Task<CategoryClassification> ClassifyCategoryAsync(
        string description, string? categoryHint = null, CancellationToken ct = default);
    Task<FamilyClassification> ClassifyFamilyAsync(
        string description, string? categoryHint = null, CancellationToken ct = default);
}

/// <summary>
/// Typed HttpClient over the DGX classifier service.
/// The service returns snake_case JSON; SnakeCaseLower handles both directions
/// and serialises {description, category_hint}. Transport / non-2xx errors are thrown;
/// the orchestrator treats any thrown classifier error as "needs review".
/// </summary>
public class HttpCategoryClassifier : ICategoryClassifier
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public HttpCategoryClassifier(HttpClient http) => _http = http;

    private sealed record ClassifyRequest(string Description, string? CategoryHint);

    public async Task<CategoryClassification> ClassifyCategoryAsync(
        string description, string? categoryHint = null, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            "/classify", new ClassifyRequest(description, categoryHint), Json, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<CategoryClassification>(Json, ct)
               ?? throw new InvalidOperationException("Classifier returned an empty body for /classify.");
    }

    public async Task<FamilyClassification> ClassifyFamilyAsync(
        string description, string? categoryHint = null, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            "/classify_family", new ClassifyRequest(description, categoryHint), Json, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<FamilyClassification>(Json, ct)
               ?? throw new InvalidOperationException("Classifier returned an empty body for /classify_family.");
    }
}
