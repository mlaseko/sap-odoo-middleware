namespace SapOdooMiddleware.Configuration;

public class CloudflareSettings
{
    public string TunnelHostname { get; set; } = string.Empty;
    public bool ValidateCfHeaders { get; set; } = true;
}
