using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.Vision;

/// <summary>
/// Typed HttpClient over the Autohub DGX parts endpoint. The base URL and endpoint path come from
/// the active tenant's config (Companies:Autohub) via <see cref="ICompanyContext"/>; the Autohub
/// extraction worker sets the scope's tenant to Autohub before this is resolved. Reuses the same
/// tolerant decimal parsing as the Lubes extractor (US/EU formats, percent, strings, nulls).
/// </summary>
public sealed class HttpPartsInvoiceExtractor : IInvoicePartsExtractor
{
    private readonly HttpClient _http;
    private readonly ICompanyContext _company;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new ResilientNullableDecimalConverter() },
    };

    public HttpPartsInvoiceExtractor(HttpClient http, ICompanyContext company)
    {
        _http = http;
        _company = company;
    }

    private sealed record ExtractRequest(string image_base64, int page_no);

    public async Task<PartsInvoicePageExtraction> ExtractPageAsync(byte[] pngBytes, int pageNo, CancellationToken ct)
    {
        var baseUrl  = _company.Current.Classifier.BaseUrl.TrimEnd('/');
        var endpoint = _company.Current.VisionEndpoint;                 // /extract_parts_invoice
        var url = $"{baseUrl}{endpoint}";

        var body = new ExtractRequest(Convert.ToBase64String(pngBytes), pageNo);
        using var resp = await _http.PostAsJsonAsync(url, body, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<PartsInvoicePageExtraction>(Json, ct)
               ?? new PartsInvoicePageExtraction { Error = "Empty response from parts vision endpoint." };
    }
}
