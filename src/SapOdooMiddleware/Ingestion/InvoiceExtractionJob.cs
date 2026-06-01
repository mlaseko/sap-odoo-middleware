using System.Diagnostics;
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
            await _docs.SetTotalPagesAsync(documentId, pages.Count, ct);

            // 2. Extract each page in isolation — one bad page must not fail the whole document.
            InvoiceHeader? header = null;
            InvoiceFooter? footer = null;
            var allLines = new List<(int pageNo, InvoiceLine line)>();
            var rawByPage = new Dictionary<int, InvoicePageExtraction>();
            var successfulPages = 0;
            var failedPages = new List<(int PageNo, string Error)>();

            for (int i = 0; i < pages.Count; i++)
            {
                int pageNo = i + 1;
                var sw = Stopwatch.StartNew();
                await _docs.MarkPageStartedAsync(documentId, pageNo, ct);
                try
                {
                    var result = await _extractor.ExtractPageAsync(pages[i], pageNo, ct);
                    rawByPage[pageNo] = result;

                    header ??= result.Header;                          // first page that has it wins
                    if (result.Footer is not null) footer = result.Footer; // last page with totals wins

                    foreach (var line in result.Lines)
                        allLines.Add((pageNo, line));

                    sw.Stop();
                    await _docs.RecordPageCompletedAsync(documentId, pageNo, sw.Elapsed.TotalSeconds, ct);
                    successfulPages++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sw.Stop();
                    _logger.LogError(ex, "Page {Page} extraction failed for document {Id}", pageNo, documentId);
                    failedPages.Add((pageNo, ex.Message));
                    // Record duration even on failure so ETA stays accurate; continue to next page.
                    await _docs.RecordPageCompletedAsync(documentId, pageNo, sw.Elapsed.TotalSeconds, ct);
                }
            }

            // If every page failed, the document as a whole is failed.
            if (successfulPages == 0)
            {
                var allFailedMsg = $"All {pages.Count} page(s) failed extraction. First error: {failedPages[0].Error}";
                _logger.LogError("Document {Id}: {Message}", documentId, allFailedMsg);
                await _docs.UpdateStatusAsync(documentId, "failed", allFailedMsg, ct);
                return;
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

            // 6. Surface any partial-page failures on the row (status remains 'extracted').
            if (failedPages.Count > 0)
            {
                var warning = $"{failedPages.Count}/{pages.Count} page(s) failed: " +
                              string.Join("; ", failedPages.Select(p => $"page {p.PageNo}: {p.Error}"));
                _logger.LogWarning("Document {Id} partial extraction: {Warning}", documentId, warning);
                // Status stays 'extracted'; record the warning in ErrorMessage so it shows on Detail.
                await _docs.UpdateStatusAsync(documentId, "extracted", warning, ct);
            }

            _logger.LogInformation(
                "Document {Id}: extraction complete — {Lines} line(s), {Ok}/{Total} page(s) ok, validation={Validation}.",
                documentId, rows.Count, successfulPages, pages.Count, validationStatus);
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
            DiscountPct:   line.DiscountPct ?? 0m,
            LineTotal:     line.LineTotal,
            IsPromotional: IsPromotional(line));

    private static bool IsPromotional(InvoiceLine line)
    {
        if ((line.DiscountPct ?? 0m) >= 100m) return true;
        if ((line.UnitPrice ?? 0m) == 0m && (line.LineTotal ?? 0m) == 0m && line.Quantity is > 0m) return true;
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
