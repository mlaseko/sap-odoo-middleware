namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Odoo JSON-RPC connection settings.
/// </summary>
public class OdooSettings
{
    public const string SectionName = "Odoo";

    /// <summary>Odoo base URL (e.g. "https://mycompany.odoo.com").</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Odoo database name (e.g. "mlaseko-molas-lubes-main-28106355").</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>Odoo user login (email). Used for logging/reference. Not required for Bearer auth.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Odoo user password. Deprecated — use ApiKey instead for Odoo 18+ Bearer auth.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Odoo API key for Bearer token authentication (Odoo 18+).
    /// If set, Bearer auth is used. If empty, falls back to classic session auth with Password.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Returns the effective API key: prefers ApiKey, falls back to Password.
    /// </summary>
    public string EffectiveApiKey => !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey : Password;

    /// <summary>
    /// Whether to use Bearer token authentication (Odoo 18+ /json/2/ style).
    /// Automatically true when ApiKey is configured.
    /// </summary>
    public bool UseBearerAuth => !string.IsNullOrWhiteSpace(ApiKey);
}
