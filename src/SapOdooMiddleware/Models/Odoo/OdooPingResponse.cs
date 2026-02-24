namespace SapOdooMiddleware.Models.Odoo;

/// <summary>
/// Response returned by GET /api/odoo/ping to indicate Odoo JSON-RPC connectivity.
/// </summary>
public class OdooPingResponse
{
    /// <summary>Whether authentication to Odoo succeeded.</summary>
    public bool Connected { get; set; }

    /// <summary>Odoo user ID (uid) returned by authentication.</summary>
    public int Uid { get; set; }

    /// <summary>Odoo database name used for authentication.</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>Odoo server version (e.g. "18.0").</summary>
    public string? ServerVersion { get; set; }

    /// <summary>Odoo base URL used for the connection.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The Odoo user login (email) used for authentication.</summary>
    public string UserName { get; set; } = string.Empty;
}
