namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Settings for the remote DGX category/family classifier service.
/// </summary>
public class ClassifierSettings
{
    public const string SectionName = "Classifier";

    /// <summary>Base URL of the DGX classifier service (e.g. "http://10.0.0.5:8077").</summary>
    public string BaseUrl { get; set; } = "http://localhost:8077";

    /// <summary>Per-request HTTP timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 180;
}
