using System.Text.Json;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Api;

namespace SapOdooMiddleware.Middleware;

/// <summary>
/// Validates X-Api-Key header on /api/* requests only.
/// All other paths (health, Swagger, favicon, unknown) skip authentication.
/// Logs authenticated API requests for observability.
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
        // Only enforce API key auth on /api/* routes.
        // Everything else (health, Swagger, favicon, bot probes, etc.) passes through
        // and will naturally 404 if no matching endpoint exists.
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        var method = context.Request.Method;
        var path = context.Request.Path;

        _logger.LogInformation(
            "Incoming request: {Method} {Path} from {RemoteIp}",
            method, path, context.Connection.RemoteIpAddress);

        if (string.IsNullOrEmpty(settings.Value.Key))
        {
            _logger.LogError(
                "API key is not configured on the server (ApiKey:Key setting is empty). " +
                "All API requests will be rejected until a key is configured. " +
                "Request: {Method} {Path} from {RemoteIp}",
                method, path, context.Connection.RemoteIpAddress);

            await WriteUnauthorizedResponse(context, "API key is not configured on the server. Contact the administrator.");
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var extractedApiKey)
            || string.IsNullOrEmpty(extractedApiKey))
        {
            _logger.LogWarning(
                "Unauthorized request: {Method} {Path} — missing X-Api-Key header from {RemoteIp}",
                method, path, context.Connection.RemoteIpAddress);

            await WriteUnauthorizedResponse(context, "Missing X-Api-Key header. Include a valid API key in the X-Api-Key request header.");
            return;
        }

        if (!string.Equals(extractedApiKey, settings.Value.Key, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Unauthorized request: {Method} {Path} — invalid API key from {RemoteIp}",
                method, path, context.Connection.RemoteIpAddress);

            await WriteUnauthorizedResponse(context, "Invalid API key.");
            return;
        }

        _logger.LogInformation(
            "Authenticated request: {Method} {Path}",
            method, path);

        await _next(context);
    }

    private static async Task WriteUnauthorizedResponse(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";

        var response = ApiResponse<object>.Fail(message);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        await context.Response.WriteAsJsonAsync(response, options);
    }
}
