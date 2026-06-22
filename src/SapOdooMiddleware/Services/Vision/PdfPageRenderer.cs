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
        var result = new List<byte[]>();
        using var pdfStream = File.OpenRead(pdfPath);

        foreach (var bitmap in Conversion.ToImages(pdfStream, options: new RenderOptions(Dpi: dpi)))
        {
            using var skBitmap = bitmap; // disposable per page
            using var data = skBitmap.Encode(SKEncodedImageFormat.Png, 90);
            result.Add(data.ToArray());
        }

        return result;
    }
}
