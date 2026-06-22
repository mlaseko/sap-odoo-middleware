using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SapOdooMiddleware.Filters;

/// <summary>
/// Global MVC exception filter (all controllers live under /api/*). Turns any unhandled exception
/// into a JSON-parseable envelope so the browser never receives a raw Npgsql/exception string
/// (which made the UI throw "Unexpected token 'N' is not valid JSON"). The real exception is logged
/// with the request id; the client gets only a generic message + the id to quote to support.
/// </summary>
public sealed class ApiExceptionFilter : IExceptionFilter
{
    private readonly ILogger<ApiExceptionFilter> _logger;
    public ApiExceptionFilter(ILogger<ApiExceptionFilter> logger) => _logger = logger;

    public void OnException(ExceptionContext context)
    {
        var requestId = context.HttpContext.TraceIdentifier;
        _logger.LogError(context.Exception,
            "Unhandled API exception (request_id={RequestId}) on {Method} {Path}",
            requestId, context.HttpContext.Request.Method, context.HttpContext.Request.Path);

        context.Result = new ObjectResult(new
        {
            success = false,
            errors = new[] { "A server error occurred. See logs." },
            request_id = requestId,
        })
        {
            StatusCode = StatusCodes.Status500InternalServerError,
        };
        context.ExceptionHandled = true;
    }
}
