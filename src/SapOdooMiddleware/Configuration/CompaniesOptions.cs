namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Binds the top-level <c>Companies</c> section: per-tenant configuration keyed by CompanyKey
/// ("Lubes", "Autohub"). The Lubes entry is metadata-only (DisplayName/IsDefault) — Lubes
/// credentials remain in the existing top-level SapB1/Odoo/Neon sections and are surfaced by
/// <see cref="ICompanyContext"/> for backward compatibility. The Autohub entry carries its own
/// connection string, storage root, and vision endpoint.
/// </summary>
public sealed class CompaniesOptions
{
    public const string SectionName = "Companies";

    public Dictionary<string, CompanyConfig> Companies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Per-tenant configuration. Reuses the existing *Settings types (no parallel option types).</summary>
public sealed class CompanyConfig
{
    public string DisplayName { get; set; } = "";

    /// <summary>True for the tenant served by the unprefixed default routes (Lubes).</summary>
    public bool IsDefault { get; set; }

    /// <summary>SAP B1 credentials. Null/unused in Autohub Phase A (extraction only).</summary>
    public SapB1Settings? SapB1 { get; set; }

    /// <summary>Odoo credentials. Null/unused in Autohub Phase A.</summary>
    public OdooSettings? Odoo { get; set; }

    public NeonSettings Neon { get; set; } = new();
    public DocumentIngestionSettings DocumentIngestion { get; set; } = new();
    public ClassifierSettings Classifier { get; set; } = new();

    /// <summary>Vision model name on the DGX (same model for both tenants today).</summary>
    public string VisionModel { get; set; } = "qwen2.5vl:32b-invoice";

    /// <summary>DGX vision endpoint path: <c>/extract_invoice</c> (Lubes) or <c>/extract_parts_invoice</c> (Autohub).</summary>
    public string VisionEndpoint { get; set; } = "/extract_invoice";
}
