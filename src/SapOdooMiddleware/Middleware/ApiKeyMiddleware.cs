using System.Text.Json;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Api;

namespace SapOdooMiddleware.Middleware;

/// <summary>
/// Validates X-Api-Key header on all requests except the health endpoint.
/// </summary>
public class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    /// <summary>
    /// Paths that are known API endpoints. Requests to unknown paths
    /// (e.g. WordPress scanner probes) are silently rejected with 404
    /// to avoid log noise.
    /// </summary>
    private static readonly string[] KnownPathPrefixes =
    [
        "/health",
        "/swagger",
        "/favicon.ico",
        "/api/"
    ];

    public ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IOptions<ApiKeySettings> settings)
    {
        var path = context.Request.Path;

        // Skip auth for health, Swagger, and browser-noise endpoints
        if (path.StartsWithSegments("/health")
            || path.StartsWithSegments("/swagger")
            || path.StartsWithSegments("/favicon.ico"))
        {
            await _next(context);
            return;
        }

        // Silently reject requests to unknown paths (bot/scanner traffic)
        if (!IsKnownPath(path))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var extractedApiKey)
            || string.IsNullOrEmpty(settings.Value.Key)
            || !string.Equals(extractedApiKey, settings.Value.Key, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Unauthorized request: {Method} {Path} - missing or invalid API key from {RemoteIp}",
                context.Request.Method, path, context.Connection.RemoteIpAddress);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";

            var response = ApiResponse<object>.Fail("Invalid or missing API key.");
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            await context.Response.WriteAsJsonAsync(response, options);
            return;
        }

        await _next(context);
    }

    private static bool IsKnownPath(PathString path)
    {
        foreach (var prefix in KnownPathPrefixes)
        {
            if (path.StartsWithSegments(prefix))
                return true;
        }

        return false;
    }
}
