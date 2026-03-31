using System.Text.Json;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Api;

namespace SapOdooMiddleware.Middleware;

/// <summary>
/// Validates API key on /api/* requests only.
/// Accepts the key via X-Api-Key header or Authorization: Bearer {token}.
/// All other paths (health, Swagger, favicon, unknown) skip authentication.
/// Logs authenticated API requests for observability.
/// </summary>
public class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";
    private const string BearerPrefix = "Bearer ";
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

        var extractedApiKey = ExtractApiKey(context);

        if (string.IsNullOrEmpty(extractedApiKey))
        {
            _logger.LogWarning(
                "Unauthorized request: {Method} {Path} — missing API key from {RemoteIp}. " +
                "Send via X-Api-Key header or Authorization: Bearer token.",
                method, path, context.Connection.RemoteIpAddress);

            await WriteUnauthorizedResponse(context, "Missing API key. Include a valid key via X-Api-Key header or Authorization: Bearer token.");
            return;
        }

        if (!string.Equals(extractedApiKey, settings.Value.Key, StringComparison.Ordinal))
        {
            var receivedPrefix = extractedApiKey.Length > 8
                ? extractedApiKey[..8] + "..."
                : extractedApiKey;
            var expectedPrefix = settings.Value.Key.Length > 8
                ? settings.Value.Key[..8] + "..."
                : settings.Value.Key;
            _logger.LogWarning(
                "Unauthorized request: {Method} {Path} — invalid API key from {RemoteIp}. " +
                "Received={ReceivedKeyPrefix} (len={ReceivedLen}), Expected={ExpectedKeyPrefix} (len={ExpectedLen})",
                method, path, context.Connection.RemoteIpAddress,
                receivedPrefix, extractedApiKey.Length,
                expectedPrefix, settings.Value.Key.Length);

            await WriteUnauthorizedResponse(context, "Invalid API key.");
            return;
        }

        _logger.LogInformation(
            "Authenticated request: {Method} {Path}",
            method, path);

        await _next(context);
    }

    /// <summary>
    /// Extracts the API key from X-Api-Key header first, then falls back to
    /// Authorization: Bearer {token}.
    /// </summary>
    private static string? ExtractApiKey(HttpContext context)
    {
        // Prefer X-Api-Key header
        if (context.Request.Headers.TryGetValue(ApiKeyHeader, out var apiKey)
            && !string.IsNullOrEmpty(apiKey))
        {
            return apiKey;
        }

        // Fall back to Authorization: Bearer {token}
        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader)
            && !string.IsNullOrEmpty(authHeader))
        {
            var headerValue = authHeader.ToString();
            if (headerValue.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var token = headerValue[BearerPrefix.Length..].Trim();
                if (!string.IsNullOrEmpty(token))
                    return token;
            }
        }

        return null;
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
