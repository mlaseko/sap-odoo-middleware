namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Odoo JSON-RPC connection settings.
/// </summary>
public class OdooSettings
{
    public const string SectionName = "Odoo";

    /// <summary>Odoo base URL (e.g. "https://mycompany.odoo.com").</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Odoo database name.</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>Odoo user login (email).</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Odoo user password or API key.</summary>
    public string Password { get; set; } = string.Empty;
}
