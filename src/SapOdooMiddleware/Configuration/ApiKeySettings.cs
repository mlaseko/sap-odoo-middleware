namespace SapOdooMiddleware.Configuration;

/// <summary>
/// API key authentication settings.
/// </summary>
public class ApiKeySettings
{
    public const string SectionName = "ApiKey";

    /// <summary>The expected API key value sent via X-Api-Key header.</summary>
    public string Key { get; set; } = string.Empty;
}
