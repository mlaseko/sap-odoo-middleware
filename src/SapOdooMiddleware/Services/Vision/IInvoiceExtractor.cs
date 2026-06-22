namespace SapOdooMiddleware.Services.Vision;

public interface IInvoiceExtractor
{
    /// <summary>
    /// Extracts structured data from a single PDF page rendered as PNG bytes.
    /// Sends the page to the DGX vision endpoint (/extract_invoice).
    /// </summary>
    Task<InvoicePageExtraction> ExtractPageAsync(byte[] pngBytes, int pageNo, CancellationToken ct);
}
