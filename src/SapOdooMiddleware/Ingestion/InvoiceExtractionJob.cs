using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// Runs the full extraction pipeline for one uploaded PDF: render → per-page vision
/// extract → replace lines → validate totals → update the document row. The document
/// row's Status (extracting → extracted/failed) is the source of truth; failures are
/// recorded on the row and swallowed so the worker loop continues.
/// </summary>
public class InvoiceExtractionJob
{
    private readonly IStagingDocumentRepository     _docs;
    private readonly IStagingDocumentLineRepository _lines;
    private readonly IPdfPageRenderer               _renderer;
    private readonly IInvoiceExtractor              _extractor;
    private readonly InvoiceTotalsValidator         _validator;
    private readonly VisionExtractorSettings        _settings;
    private readonly ILogger<InvoiceExtractionJob>  _logger;

    public InvoiceExtractionJob(
        IStagingDocumentRepository docs,
        IStagingDocumentLineRepository lines,
        IPdfPageRenderer renderer,
        IInvoiceExtractor extractor,
        InvoiceTotalsValidator validator,
        IOptions<VisionExtractorSettings> settings,
        ILogger<InvoiceExtractionJob> logger)
    {
        _docs      = docs;
        _lines     = lines;
        _renderer  = renderer;
        _extractor = extractor;
        _validator = validator;
        _settings  = settings.Value;
        _logger    = logger;
    }

    public async Task RunAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null)
        {
            _logger.LogWarning("Extraction skipped: document {Id} not found.", documentId);
            return;
        }
        if (doc.Status == "extracted")
        {
            _logger.LogInformation("Extraction skipped: document {Id} already extracted.", documentId);
            return;
        }

        await _docs.UpdateStatusAsync(documentId, "extracting", null, ct);

        try
        {
            // 1. Render PDF to PNGs.
            var pages = _renderer.RenderToPngs(doc.FilePath, _settings.PdfRenderDpi);
            _logger.LogInformation("Document {Id}: {Pages} page(s) rendered.", documentId, pages.Count);

            // 2. Extract each page, accumulate.
            InvoiceHeader? header = null;
            InvoiceFooter? footer = null;
            var allLines = new List<(int pageNo, InvoiceLine line)>();
            var rawByPage = new Dictionary<int, InvoicePageExtraction>();

            for (int i = 0; i < pages.Count; i++)
            {
                int pageNo = i + 1;
                var result = await _extractor.ExtractPageAsync(pages[i], pageNo, ct);
                rawByPage[pageNo] = result;

                header ??= result.Header;                          // first page that has it wins
                if (result.Footer is not null) footer = result.Footer; // last page with totals wins

                foreach (var line in result.Lines)
                    allLines.Add((pageNo, line));
            }

            // 3. Persist lines (replace any prior for this doc → idempotent re-extract).
            await _lines.DeleteByDocumentAsync(documentId, ct);
            var rows = allLines.Select((t, idx) => MapToRow(documentId, idx + 1, t.pageNo, t.line)).ToList();
            await _lines.InsertManyAsync(documentId, rows, ct);

            // 4. Validate totals.
            var (validationStatus, validationNotes) = _validator.Validate(allLines.Select(t => t.line), footer);

            // 5. Update document with extraction results.
            var rawJson = JsonSerializer.Serialize(rawByPage);
            await _docs.UpdateAfterExtractionAsync(
                documentId,
                pageCount: pages.Count,
                header: ToHeaderUpdate(header),
                footer: ToFooterUpdate(footer),
                validationStatus: validationStatus,
                validationNotes: validationNotes,
                rawExtractionJson: rawJson,
                ct: ct);

            _logger.LogInformation(
                "Document {Id}: extraction complete — {Lines} line(s), validation={Validation}.",
                documentId, rows.Count, validationStatus);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Extraction failed for document {Id}", documentId);
            try { await _docs.UpdateStatusAsync(documentId, "failed", ex.Message, ct); }
            catch (Exception inner) { _logger.LogError(inner, "Also failed to mark document {Id} failed.", documentId); }
        }
    }

    private static StagingDocumentLineRow MapToRow(Guid docId, int lineNo, int pageNo, InvoiceLine line)
        => new(
            Id:            Guid.NewGuid(),
            DocumentId:    docId,
            LineNo:        lineNo,
            PageNo:        pageNo,
            ArticleNumber: line.ArticleNumber,
            Description:   line.Description,
            PackSize:      line.PackSize,
            UnitPrice:     line.UnitPrice,
            Quantity:      line.Quantity,
            Unit:          line.Unit,
            CommodityCode: line.CommodityCode,
            Origin:        line.Origin,
            DiscountPct:   line.DiscountPct,
            LineTotal:     line.LineTotal,
            IsPromotional: IsPromotional(line));

    private static bool IsPromotional(InvoiceLine line)
    {
        if (line.DiscountPct >= 100m) return true;
        if (line.UnitPrice is null or 0m) return true;
        if (string.Equals(line.PackSize?.Trim(), "1 Stk", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(line.Unit?.Trim(), "Stk", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static InvoiceHeaderUpdate? ToHeaderUpdate(InvoiceHeader? h) => h is null ? null : new(
        InvoiceNumber:    h.InvoiceNumber,
        InvoiceDate:      ParseDate(h.InvoiceDate),
        SalesOrder:       h.SalesOrder,
        DeliveryNoteRef:  h.DeliveryNoteRef,
        CustomerName:     h.CustomerName,
        CustomerAccount:  h.CustomerAccount,
        Currency:         h.Currency);

    private static InvoiceFooterUpdate? ToFooterUpdate(InvoiceFooter? f) => f is null ? null : new(
        Subtotal:     f.Subtotal,
        Freight:      f.Freight,
        TotalNet:     f.TotalNet,
        TaxAmount:    f.TaxAmount,
        InvoiceTotal: f.InvoiceTotal,
        PaymentTerms: f.PaymentTerms,
        DueDate:      ParseDate(f.DueDate));

    private static DateTime? ParseDate(string? s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
}
