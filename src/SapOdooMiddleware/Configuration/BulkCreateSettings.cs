namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Settings for Lubes bulk item creation. The per-item timeout caps how long one line's full
/// provisioning (Liqui Moly scrape + DGX classifier /classify + /classify_family + SAP write) may
/// run before the batch gives up on it and marks it 'create_failed' (retried on the next Bulk Create).
/// Default 120s matches the autohub batch; the DGX classifier alone is allowed up to
/// <see cref="ClassifierSettings.TimeoutSeconds"/> (180s), so keep this comfortably above that if the
/// classifier is frequently cold.
/// </summary>
public class BulkCreateSettings
{
    public const string SectionName = "BulkCreate";

    /// <summary>Per-line provisioning timeout in seconds (default 120).</summary>
    public int PerItemTimeoutSeconds { get; set; } = 120;
}
