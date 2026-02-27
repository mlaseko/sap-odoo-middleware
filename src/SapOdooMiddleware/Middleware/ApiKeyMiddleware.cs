using System.Text.Json;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Api;

namespace SapOdooMiddleware.Middleware;

/// <summary>
/// Validates X-Api-Key header on all requests except the health endpoint.
/// Logs every incoming request for observability.
/// </summary>
public class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IOptions<ApiKeySettings> settings)
    {
        var method = context.Request.Method;
        var path = context.Request.Path;

        _logger.LogInformation(
            "Incoming request: {Method} {Path} from {RemoteIp}",
            method, path, context.Connection.RemoteIpAddress);

        // Skip auth for health and Swagger endpoints
        if (context.Request.Path.StartsWithSegments("/health")
            || context.Request.Path.StartsWithSegments("/swagger"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var extractedApiKey)
            || string.IsNullOrEmpty(settings.Value.Key)
            || !string.Equals(extractedApiKey, settings.Value.Key, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Unauthorized request: {Method} {Path} — missing or invalid API key from {RemoteIp}",
                method, path, context.Connection.RemoteIpAddress);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";

            var response = ApiResponse<object>.Fail("Invalid or missing API key.");
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            await context.Response.WriteAsJsonAsync(response, options);
            return;
        }

        _logger.LogInformation(
            "Authenticated request: {Method} {Path}",
            method, path);

        await _next(context);
    }
}
