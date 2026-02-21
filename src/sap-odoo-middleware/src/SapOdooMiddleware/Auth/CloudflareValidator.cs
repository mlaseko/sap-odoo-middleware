using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Auth;

public class CloudflareValidator
{
    private readonly RequestDelegate _next;
    private readonly AuthSettings _authSettings;
    private readonly ILogger<CloudflareValidator> _logger;

    private static readonly HashSet<string> ExcludedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/health",
        "/swagger"
    };

    public CloudflareValidator(RequestDelegate next, IOptions<AuthSettings> authSettings, ILogger<CloudflareValidator> logger)
    {
        _next = next;
        _authSettings = authSettings.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (!_authSettings.RequireCloudflareHeaders ||
            ExcludedPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.ContainsKey("CF-Ray"))
        {
            _logger.LogWarning("Request to {Path} did not come through Cloudflare (missing CF-Ray)", path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { success = false, errors = new[] { new { code = "AUTH_NOT_CLOUDFLARE", message = "Request must come through Cloudflare tunnel" } } });
            return;
        }

        await _next(context);
    }
}
