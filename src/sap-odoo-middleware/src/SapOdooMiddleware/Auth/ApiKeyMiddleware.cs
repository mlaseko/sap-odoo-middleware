using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Auth;

public class ApiKeyMiddleware
{
    private const string ApiKeyHeaderName = "X-Api-Key";
    private readonly RequestDelegate _next;
    private readonly AuthSettings _authSettings;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    private static readonly HashSet<string> ExcludedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/health",
        "/api/health/detailed",
        "/swagger",
        "/swagger/index.html",
        "/swagger/v1/swagger.json"
    };

    public ApiKeyMiddleware(RequestDelegate next, IOptions<AuthSettings> authSettings, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _authSettings = authSettings.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (ExcludedPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var extractedApiKey))
        {
            _logger.LogWarning("API key missing from request to {Path}", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { success = false, errors = new[] { new { code = "AUTH_INVALID_KEY", message = "Missing X-Api-Key header" } } });
            return;
        }

        if (!string.Equals(extractedApiKey, _authSettings.ApiKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("Invalid API key provided for request to {Path}", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { success = false, errors = new[] { new { code = "AUTH_INVALID_KEY", message = "Invalid API key" } } });
            return;
        }

        await _next(context);
    }
}
