namespace SapOdooMiddleware.Configuration;

/// <summary>
/// SAP B1 DI API connection settings.
/// </summary>
public class SapB1Settings
{
    public const string SectionName = "SapB1";

    /// <summary>SQL Server hostname or IP (Cloudflare tunnel endpoint).</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>SAP B1 company database name.</summary>
    public string CompanyDb { get; set; } = string.Empty;

    /// <summary>SAP B1 user name.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>SAP B1 user password.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>SQL Server type (e.g. dst_MSSQL2019).</summary>
    public string DbServerType { get; set; } = "dst_MSSQL2019";

    /// <summary>License server address (host:port).</summary>
    public string LicenseServer { get; set; } = string.Empty;

    /// <summary>SLD Server address (host:port, e.g. "WIN-GJGQ73V0C3K:40000"). Leave empty to skip.</summary>
    public string SLDServer { get; set; } = string.Empty;

    /// <summary>Whether to auto-create a pick list after SO creation.</summary>
    public bool AutoCreatePickList { get; set; } = true;
}
