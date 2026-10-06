using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Diagnostics;
using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Runtime/deployment self-inspection (API-key protected, like all /api routes). Answers "why does PDF
/// upload fail with 'Could not load file or assembly …' on this server?" in one request: process bitness,
/// framework, self-contained vs framework-dependent, where System.Drawing.Primitives resolves from, the
/// native pdfium/libSkiaSharp files present, and a real render of a tiny embedded PDF. Read-only apart from
/// a temporary probe file. Separate from AdminController so that constructor is untouched.
/// </summary>
[ApiController]
[Route("api/admin/runtime-diagnostics")]
public sealed class RuntimeDiagnosticsController : ControllerBase
{
    private readonly IPdfPageRenderer _renderer;

    public RuntimeDiagnosticsController(IPdfPageRenderer renderer) => _renderer = renderer;

    /// <summary>GET /api/admin/runtime-diagnostics[?render=false] — render=false skips the PDF render test.</summary>
    [HttpGet]
    public ActionResult<RuntimeDiagnosticsReport> Get([FromQuery] bool render = true)
        => Ok(RuntimeDiagnostics.Collect(render ? _renderer : null));
}
