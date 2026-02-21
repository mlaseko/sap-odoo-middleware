using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace SapOdooMiddleware.Filters;

/// <summary>
/// Applies the ApiKey security requirement to all Swagger operations except
/// the /health endpoint and /swagger/* paths (which are exempt from auth).
/// </summary>
public class ApiKeyOperationFilter : IOperationFilter
{
    private static readonly OpenApiSecurityRequirement SecurityRequirement = new()
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
            },
            Array.Empty<string>()
        }
    };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath ?? string.Empty;

        if (path.Equals("health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("swagger", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        operation.Security ??= new List<OpenApiSecurityRequirement>();
        operation.Security.Add(SecurityRequirement);
    }
}
