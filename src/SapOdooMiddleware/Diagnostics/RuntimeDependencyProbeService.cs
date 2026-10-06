using System.Runtime.InteropServices;

namespace SapOdooMiddleware.Diagnostics;

/// <summary>
/// Startup check for the PDF rendering dependency that has failed in the field: System.Drawing.Primitives,
/// a .NET 8 framework assembly referenced by PDFtoImage. Logs the runtime shape once, and a CRITICAL line with
/// the remedy if the assembly cannot be loaded — so a broken deployment shows up in the log at start instead of
/// on the first invoice upload. Managed-only on purpose: it never loads the pdfium/SkiaSharp natives at startup
/// (GET /api/admin/runtime-diagnostics runs the full render test on demand). Never blocks or fails startup.
/// </summary>
public sealed class RuntimeDependencyProbeService : IHostedService
{
    private readonly ILogger<RuntimeDependencyProbeService> _logger;

    public RuntimeDependencyProbeService(ILogger<RuntimeDependencyProbeService> logger) => _logger = logger;

    public Task StartAsync(CancellationToken ct)
    {
        try
        {
            var probe = RuntimeDiagnostics.ProbeSystemDrawingPrimitives();
            if (probe.Ok)
            {
                _logger.LogInformation(
                    "Runtime: {Framework} {ProcessArch} (OS {OsArch}), base {BaseDirectory}; System.Drawing.Primitives {Version} from {Location}.",
                    RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture,
                    RuntimeInformation.OSArchitecture, AppContext.BaseDirectory, probe.Version, probe.Location);
            }
            else
            {
                _logger.LogCritical(
                    "[FTL] PDF invoice extraction WILL FAIL: System.Drawing.Primitives (part of the .NET 8 runtime) cannot be loaded ({Error}). " +
                    "Runtime {Framework} {ProcessArch}, base {BaseDirectory}. The deployment is incomplete or mixes different publishes: " +
                    "stop the service, empty the publish folder, publish once with a single profile, restart. " +
                    "Details: GET /api/admin/runtime-diagnostics.",
                    probe.Error, RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture,
                    AppContext.BaseDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Runtime dependency probe could not run.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
