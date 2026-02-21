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

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IOptions<ApiKeySettings> settings)
    {
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
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";

            var response = ApiResponse<object>.Fail("Invalid or missing API key.");
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            await context.Response.WriteAsJsonAsync(response, options);
            return;
        }

        await _next(context);
    }
}
