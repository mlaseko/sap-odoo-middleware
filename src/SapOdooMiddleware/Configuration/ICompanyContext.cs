namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Per-request (or per-worker-scope) tenant context. The active company is set by
/// <c>TenantResolutionMiddleware</c> from the URL prefix for HTTP requests, or explicitly by the
/// Autohub background worker for its scope. Defaults to "Lubes" so existing unprefixed URLs and
/// any code that never sets a tenant keep working unchanged.
/// </summary>
public interface ICompanyContext
{
    /// <summary>The active CompanyKey, e.g. "Lubes" or "Autohub".</summary>
    string CurrentCompanyKey { get; }

    /// <summary>Resolved configuration for the active company.</summary>
    CompanyConfig Current { get; }
}
