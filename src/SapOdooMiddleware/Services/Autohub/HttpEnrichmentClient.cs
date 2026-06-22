using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.Autohub;

public interface IEnrichmentClient
{
    Task<EnrichmentResponse> EnrichAsync(EnrichmentRequest request, CancellationToken ct);
}

/// <summary>
/// Typed HttpClient over the Autohub DGX <c>/enrich_item</c> endpoint. Base URL is resolved from the
/// active tenant (Companies:Autohub:Classifier:BaseUrl) via <see cref="ICompanyContext"/>; the
/// AutoMatch/enrichment callers run in an Autohub-pinned scope. Long timeout is configured at
/// registration (enrichment can trigger a Germax scrape on a cold item).
/// </summary>
public sealed class HttpEnrichmentClient : IEnrichmentClient
{
    private readonly HttpClient _http;
    private readonly ICompanyContext _company;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public HttpEnrichmentClient(HttpClient http, ICompanyContext company)
    {
        _http = http;
        _company = company;
    }

    public async Task<EnrichmentResponse> EnrichAsync(EnrichmentRequest request, CancellationToken ct)
    {
        var url = $"{_company.Current.Classifier.BaseUrl.TrimEnd('/')}/enrich_item";
        using var resp = await _http.PostAsJsonAsync(url, request, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<EnrichmentResponse>(Json, ct)
               ?? throw new InvalidOperationException("Empty response from /enrich_item.");
    }
}
