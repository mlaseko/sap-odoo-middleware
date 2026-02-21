namespace SapOdooMiddleware.Configuration;

public class AuthSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public bool RequireCloudflareHeaders { get; set; } = true;
}
