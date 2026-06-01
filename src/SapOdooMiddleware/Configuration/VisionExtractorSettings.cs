namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Settings for the DGX vision extractor used to read invoice pages.
/// The base URL is reused from <c>ClassifierSettings.BaseUrl</c> (same DGX host).
/// </summary>
public class VisionExtractorSettings
{
    public const string SectionName = "VisionExtractor";

    /// <summary>HTTP timeout for the DGX vision call. qwen2.5vl:32b can take 30-90s per page.</summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>DPI used when rasterising PDF pages to PNG before sending to the vision model.</summary>
    public int PdfRenderDpi { get; set; } = 200;

    /// <summary>Tolerance (EUR) when validating Σ(line_total) + freight ≈ total_net.</summary>
    public decimal TotalsToleranceEur { get; set; } = 1.00m;
}
