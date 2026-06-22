using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Middleware;

/// <summary>
/// Sets the per-request tenant on the scoped <see cref="CompanyContext"/> from the URL prefix
/// (/autohub or /api/autohub → Autohub; everything else → Lubes). Runs early, before any
/// tenant-aware service is resolved by a controller or Razor page.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext context, CompanyContext company)
    {
        company.SetCompany(CompanyContext.ResolveCompanyKey(context.Request.Path));
        await _next(context);
    }
}
