using System.Runtime.CompilerServices;
using PDFtoImage;
using SkiaSharp;

namespace SapOdooMiddleware.Services.Vision;

public interface IPdfPageRenderer
{
    /// <summary>Renders each page of the PDF to a PNG byte array. Index 0 = page 1.</summary>
    IReadOnlyList<byte[]> RenderToPngs(string pdfPath, int dpi);
}

/// <summary>
/// Rasterises PDF pages to PNG via PDFtoImage (SkiaSharp). SkiaSharp is Windows-friendly;
/// native assets ship with the package. NOTE: the PDFtoImage API has shifted across major
/// versions — if this does not compile against the installed package, adapt the call; the
/// contract is simply "PDF path in → ordered list of PNG byte arrays at the requested DPI".
/// </summary>
public class PdfPageRenderer : IPdfPageRenderer
{
    public IReadOnlyList<byte[]> RenderToPngs(string pdfPath, int dpi)
    {
        using var pdfStream = File.OpenRead(pdfPath);
        try
        {
            return RenderCore(pdfStream, dpi);
        }
        catch (Exception ex) when (RenderDependencyFault.IsLoadFailure(ex))
        {
            // Surfaces on the document (ErrorMessage) — say it is a deployment problem, not a bad invoice.
            throw new InvalidOperationException(RenderDependencyFault.Describe(ex), ex);
        }
    }

    // Kept out of line on purpose: to compile this method the JIT must lay out PDFtoImage's RenderOptions,
    // whose Bounds field is a System.Drawing.RectangleF? (System.Drawing.Primitives, a .NET 8 framework
    // assembly). If the deployment cannot supply that assembly — or the PDFium/SkiaSharp natives — the
    // failure is raised when RenderToPngs CALLS this method, inside the try above, rather than before
    // RenderToPngs even starts.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IReadOnlyList<byte[]> RenderCore(Stream pdfStream, int dpi)
    {
        var result = new List<byte[]>();

        foreach (var bitmap in Conversion.ToImages(pdfStream, options: new RenderOptions(Dpi: dpi)))
        {
            using var skBitmap = bitmap; // disposable per page
            using var data = skBitmap.Encode(SKEncodedImageFormat.Png, 90);
            result.Add(data.ToArray());
        }

        return result;
    }
}

/// <summary>
/// Classifies exceptions from the PDF rendering stack that mean a runtime component (a .NET framework
/// assembly such as System.Drawing.Primitives, or the native pdfium / libSkiaSharp libraries) could not be
/// loaded — i.e. a broken or mixed deployment rather than a problem with the PDF itself.
/// </summary>
internal static class RenderDependencyFault
{
    public static bool IsLoadFailure(Exception ex) => ex switch
    {
        // SkiaSharp's static initializer throws InvalidOperationException when libSkiaSharp is present but a
        // different version from SkiaSharp.dll — the typical mixed-publish native fault.
        TypeInitializationException { TypeName: { } t, InnerException: InvalidOperationException }
            when t.StartsWith("SkiaSharp.", StringComparison.Ordinal) => true,
        TypeInitializationException { InnerException: { } inner } => IsLoadFailure(inner),
        FileNotFoundException or FileLoadException => true,   // assembly could not be found / loaded
        DllNotFoundException or EntryPointNotFoundException => true,   // native library missing / wrong version
        BadImageFormatException => true,   // x86/x64 mismatch between the process and a DLL
        TypeLoadException or MissingMethodException or MissingFieldException => true,   // mismatched assembly versions
        _ => false,
    };

    /// <summary>The underlying fault, with any type-initializer wrappers removed.</summary>
    public static Exception Root(Exception ex)
    {
        while (ex is TypeInitializationException { InnerException: { } inner })
            ex = inner;
        return ex;
    }

    public static string Describe(Exception ex)
    {
        var root = Root(ex);
        return "PDF rendering is unavailable on this server: a runtime component could not be loaded "
             + $"({root.GetType().Name}: {root.Message}). This is a server problem, not a problem with the invoice. "
             + "First restart the SapOdooMiddleware service (a .NET or Visual Studio update can remove runtime files "
             + "from under a running service), then delete this document and upload it again. If it still fails, "
             + "GET /api/admin/runtime-diagnostics shows what the running process can load.";
    }
}
