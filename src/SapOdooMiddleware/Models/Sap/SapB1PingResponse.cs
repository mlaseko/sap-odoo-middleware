namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response returned by GET /api/sapb1/ping to indicate SAP B1 DI API connectivity.
/// </summary>
public class SapB1PingResponse
{
    /// <summary>Whether the middleware is currently connected to SAP B1.</summary>
    public bool Connected { get; set; }

    /// <summary>SAP B1 server hostname or IP.</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>SAP B1 company database name.</summary>
    public string CompanyDb { get; set; } = string.Empty;

    /// <summary>License server address.</summary>
    public string LicenseServer { get; set; } = string.Empty;

    /// <summary>SLD server address (empty if not configured).</summary>
    public string SldServer { get; set; } = string.Empty;

    /// <summary>Company name as reported by SAP B1 (null if unavailable).</summary>
    public string? CompanyName { get; set; }

    /// <summary>SAP B1 server version as reported by the DI API (null if unavailable).</summary>
    public string? Version { get; set; }
}
