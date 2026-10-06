using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Diagnostics;

/// <summary>Outcome of loading one assembly by full name.</summary>
public sealed record AssemblyLoadProbe(bool Ok, string? Location, string? Version, string? Error);

/// <summary>Outcome of rendering a tiny embedded one-page PDF through the real renderer.</summary>
public sealed record PdfRenderProbe(bool Ok, int? Pages, int? PngBytes, string? Error);

/// <summary>What the running process can see and load — enough to tell a broken deployment apart.</summary>
public sealed record RuntimeDiagnosticsReport(
    string FrameworkDescription,
    string ProcessArchitecture,
    string OsArchitecture,
    string OsDescription,
    string RuntimeIdentifier,
    int ProcessId,
    string? ProcessPath,
    bool IsWindowsService,
    string BaseDirectory,
    string? CoreLibDirectory,
    bool LooksSelfContained,
    bool MixedLayoutSuspected,
    string? DepsRuntimeTarget,
    bool? DepsJsonListsSystemDrawingPrimitives,
    string? RuntimeConfigJson,
    IReadOnlyDictionary<string, bool> BaseDirectoryFiles,
    IReadOnlyList<string> NativeLibraries,
    int TrustedPlatformAssemblyCount,
    IReadOnlyList<string> SystemDrawingPrimitivesInTpa,
    AssemblyLoadProbe SystemDrawingPrimitives,
    PdfRenderProbe? PdfRender,
    string Verdict);

/// <summary>
/// Runtime/deployment self-inspection for the PDF rendering stack. PDFtoImage (every version) references
/// System.Drawing.Primitives 8.0.0.0, which ships inside the .NET 8 runtime itself (no NuGet package can
/// supply it), and loads the native pdfium / libSkiaSharp libraries — so a "could not load file or
/// assembly" during PDF upload means the process cannot see part of its own runtime or natives.
/// </summary>
public static class RuntimeDiagnostics
{
    public const string SystemDrawingPrimitivesName =
        "System.Drawing.Primitives, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a";

    // One 72x72pt blank page. Explicit \n escapes keep the xref byte offsets exact whatever line endings
    // the source file is checked out with.
    private const string MinimalPdf =
        "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\n" +
        "endobj\n3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>\nendobj\nxref\n0 4\n" +
        "0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n0000000115 00000 n \n" +
        "trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n184\n%%EOF\n";

    private static readonly string[] KeyFiles =
    {
        "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "System.Private.CoreLib.dll",
        "System.Drawing.Primitives.dll", "PDFtoImage.dll", "SkiaSharp.dll", "pdfium.dll", "libSkiaSharp.dll",
    };

    /// <summary>Load System.Drawing.Primitives by name (no compile-time reference, so failure is catchable).</summary>
    public static AssemblyLoadProbe ProbeSystemDrawingPrimitives()
    {
        try
        {
            var asm = Assembly.Load(new AssemblyName(SystemDrawingPrimitivesName));
            return new AssemblyLoadProbe(true, asm.Location, asm.GetName().Version?.ToString(), null);
        }
        catch (Exception ex)
        {
            return new AssemblyLoadProbe(false, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Render the embedded one-page PDF through the real renderer (loads PDFium + SkiaSharp natives).</summary>
    public static PdfRenderProbe ProbePdfRender(IPdfPageRenderer renderer)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sapodoo-render-probe-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllText(path, MinimalPdf);
            var pages = renderer.RenderToPngs(path, 36);
            return new PdfRenderProbe(true, pages.Count, pages.Sum(p => p.Length), null);
        }
        catch (Exception ex)
        {
            // The renderer wraps load failures in an InvalidOperationException; report the underlying fault,
            // including what a type initializer (e.g. SkiaSharp's native version check) actually threw.
            var root = RenderDependencyFault.Root(ex is InvalidOperationException { InnerException: { } inner } ? inner : ex);
            return new PdfRenderProbe(false, null, null, $"{root.GetType().Name}: {root.Message}");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    public static RuntimeDiagnosticsReport Collect(IPdfPageRenderer? renderer)
    {
        var baseDir = AppContext.BaseDirectory;
        var entryName = Assembly.GetEntryAssembly()?.GetName().Name;

        var files = KeyFiles.ToDictionary(f => f, f => File.Exists(Path.Combine(baseDir, f)));

        // Self-contained = the runtime (CoreLib) actually loaded from the app folder. Host files lying in a
        // framework-dependent folder are leftovers from an earlier self-contained publish (a mixed folder).
        var coreLibDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var looksSelfContained = coreLibDir is not null && string.Equals(
            Path.TrimEndingDirectorySeparator(coreLibDir), Path.TrimEndingDirectorySeparator(baseDir),
            StringComparison.OrdinalIgnoreCase);
        var mixedLayout = !looksSelfContained && (files["hostfxr.dll"] || files["coreclr.dll"]);

        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var sdpInTpa = tpa
            .Where(p => string.Equals(Path.GetFileName(p), "System.Drawing.Primitives.dll", StringComparison.OrdinalIgnoreCase))
            .ToList();

        string? depsTarget = null;
        bool? depsListsSdp = null;
        string? runtimeConfig = null;
        if (entryName is not null)
        {
            var depsPath = Path.Combine(baseDir, entryName + ".deps.json");
            if (File.Exists(depsPath))
            {
                try
                {
                    var depsText = File.ReadAllText(depsPath);
                    depsListsSdp = depsText.Contains("System.Drawing.Primitives.dll", StringComparison.OrdinalIgnoreCase);
                    using var json = JsonDocument.Parse(depsText);
                    if (json.RootElement.TryGetProperty("runtimeTarget", out var rt) && rt.TryGetProperty("name", out var name))
                        depsTarget = name.GetString();
                }
                catch (Exception ex) { depsTarget = $"(unreadable: {ex.Message})"; }
            }

            var rcPath = Path.Combine(baseDir, entryName + ".runtimeconfig.json");
            if (File.Exists(rcPath))
            {
                try { runtimeConfig = File.ReadAllText(rcPath); }
                catch (Exception ex) { runtimeConfig = $"(unreadable: {ex.Message})"; }
            }
        }

        var natives = new List<string>();
        var runtimesDir = Path.Combine(baseDir, "runtimes");
        if (Directory.Exists(runtimesDir))
        {
            try
            {
                natives.AddRange(Directory.EnumerateFiles(runtimesDir, "*.dll", SearchOption.AllDirectories)
                    .Where(p => Path.GetFileName(p) is var n
                                && (n.Equals("pdfium.dll", StringComparison.OrdinalIgnoreCase)
                                    || n.Equals("libSkiaSharp.dll", StringComparison.OrdinalIgnoreCase)))
                    .Select(p => Path.GetRelativePath(baseDir, p)));
            }
            catch (Exception ex) { natives.Add($"(could not list runtimes/: {ex.Message})"); }
        }

        var sdp = ProbeSystemDrawingPrimitives();
        var render = renderer is null ? null : ProbePdfRender(renderer);

        return new RuntimeDiagnosticsReport(
            FrameworkDescription: RuntimeInformation.FrameworkDescription,
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            OsArchitecture: RuntimeInformation.OSArchitecture.ToString(),
            OsDescription: RuntimeInformation.OSDescription,
            RuntimeIdentifier: RuntimeInformation.RuntimeIdentifier,
            ProcessId: Environment.ProcessId,
            ProcessPath: Environment.ProcessPath,
            IsWindowsService: Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService(),
            BaseDirectory: baseDir,
            CoreLibDirectory: coreLibDir,
            LooksSelfContained: looksSelfContained,
            MixedLayoutSuspected: mixedLayout,
            DepsRuntimeTarget: depsTarget,
            DepsJsonListsSystemDrawingPrimitives: depsListsSdp,
            RuntimeConfigJson: runtimeConfig,
            BaseDirectoryFiles: files,
            NativeLibraries: natives,
            TrustedPlatformAssemblyCount: tpa.Length,
            SystemDrawingPrimitivesInTpa: sdpInTpa,
            SystemDrawingPrimitives: sdp,
            PdfRender: render,
            Verdict: Verdict(sdp, render, looksSelfContained, mixedLayout, baseDir));
    }

    private static string Verdict(AssemblyLoadProbe sdp, PdfRenderProbe? render, bool selfContained, bool mixed, string baseDir)
    {
        if (render is { Ok: true })
            return "PDF rendering works in this process.";

        var mixedNote = mixed
            ? " Note: the folder also holds self-contained host files (hostfxr/coreclr) from an earlier publish — empty it before the next publish."
            : "";

        if (!sdp.Ok)
            return selfContained
                ? "System.Drawing.Primitives (part of the .NET 8 runtime) cannot be loaded. This is a self-contained "
                  + $"deployment, so the file must be in {baseDir} and listed in the .deps.json: the folder is "
                  + "incomplete or mixes different publishes. Stop the service, empty the folder, publish once, restart."
                : "System.Drawing.Primitives (part of the .NET 8 runtime) cannot be loaded. This is a framework-dependent "
                  + "deployment, so it comes from the shared .NET runtime (see core_lib_directory). First restart the service: "
                  + "a .NET or Visual Studio update can replace that runtime folder under a running process. If it still fails "
                  + "after a restart, repair or reinstall the .NET 8 Hosting Bundle matching process_architecture." + mixedNote;

        if (render is { Ok: false })
            return "Managed assemblies load, but the PDF render test failed — check that pdfium.dll and libSkiaSharp.dll "
                 + "match process_architecture (see native_libraries) and that the folder holds a single publish." + mixedNote;

        return "System.Drawing.Primitives loads (PDF render test not run).";
    }
}
