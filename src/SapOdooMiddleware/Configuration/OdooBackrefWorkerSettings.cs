namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Settings for the background worker that stamps the Odoo product id back onto
/// the SAP item UDF once the Neon → Odoo automation has created the product.
/// </summary>
public class OdooBackrefWorkerSettings
{
    public const string SectionName = "OdooBackrefWorker";

    /// <summary>When false, the worker logs and exits without polling.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Poll interval in seconds (floored at 30s at runtime).</summary>
    public int PollIntervalSeconds { get; set; } = 300;
}
