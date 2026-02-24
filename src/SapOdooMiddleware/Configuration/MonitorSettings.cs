namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Settings for the Odoo delivery webhook monitor callback.
/// When enabled, the middleware POSTs status updates to the Integration
/// Control Center in Odoo so delivery webhook activity can be monitored.
/// </summary>
public class MonitorSettings
{
    public const string SectionName = "Monitor";

    /// <summary>Whether to send delivery status notifications to Odoo.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Full URL of the Odoo JSON controller endpoint, e.g.
    /// "https://mycompany.odoo.com/integration/webhook/delivery-status".
    /// </summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>
    /// API key used to authenticate with the Integration Control Center.
    /// Must match an integration.backend record's api_key in Odoo.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}
