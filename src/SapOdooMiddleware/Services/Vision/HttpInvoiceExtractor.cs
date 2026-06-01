using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Services.Vision;

/// <summary>
/// Typed HttpClient over the DGX vision endpoint (/extract_invoice). The base address and
/// long timeout are configured in Program.cs from ClassifierSettings.BaseUrl +
/// VisionExtractorSettings.TimeoutSeconds. The model returns snake_case JSON and may emit
/// numbers as strings (e.g. "1,068.00"), so reading-from-string is enabled.
/// </summary>
public class HttpInvoiceExtractor : IInvoiceExtractor
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // Tolerant parsing of LLM-derived numbers (null, US/EU formats, percent, empty -> null).
        Converters = { new ResilientNullableDecimalConverter() },
    };

    public HttpInvoiceExtractor(HttpClient http) => _http = http;

    private sealed record ExtractRequest(string image_base64, int page_no);

    public async Task<InvoicePageExtraction> ExtractPageAsync(byte[] pngBytes, int pageNo, CancellationToken ct)
    {
        var body = new ExtractRequest(Convert.ToBase64String(pngBytes), pageNo);
        using var resp = await _http.PostAsJsonAsync("/extract_invoice", body, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<InvoicePageExtraction>(Json, ct)
               ?? new InvoicePageExtraction { Error = "Empty response from vision endpoint." };
    }
}
