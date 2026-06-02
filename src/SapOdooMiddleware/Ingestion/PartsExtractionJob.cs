using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// Extraction pipeline for one Autohub (spare-parts) PDF: render → per-page vision extract →
/// replace lines → validate → update the row. Same shape and per-page failure isolation as the
/// Lubes job, adapted to the parts schema (OEM arrays, foreign-currency prices, supplier header).
/// </summary>
public class PartsExtractionJob
{
    private readonly IStagingPartsDocumentRepository _docs;
    private readonly IStagingPartsLineRepository     _lines;
    private readonly IPdfPageRenderer                _renderer;
    private readonly IInvoicePartsExtractor          _extractor;
    private readonly PartsInvoiceValidator           _validator;
    private readonly VisionExtractorSettings         _settings;
    private readonly ILogger<PartsExtractionJob>     _logger;

    public PartsExtractionJob(
        IStagingPartsDocumentRepository docs,
        IStagingPartsLineRepository lines,
        IPdfPageRenderer renderer,
        IInvoicePartsExtractor extractor,
        PartsInvoiceValidator validator,
        IOptions<VisionExtractorSettings> settings,
        ILogger<PartsExtractionJob> logger)
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
            _logger.LogWarning("Parts extraction skipped: document {Id} not found.", documentId);
            return;
        }
        if (doc.Status == "extracted")
        {
            _logger.LogInformation("Parts extraction skipped: document {Id} already extracted.", documentId);
            return;
        }

        await _docs.UpdateStatusAsync(documentId, "extracting", null, ct);

        try
        {
            var pages = _renderer.RenderToPngs(doc.FilePath, _settings.PdfRenderDpi);
            _logger.LogInformation("Parts document {Id}: {Pages} page(s) rendered.", documentId, pages.Count);
            await _docs.SetTotalPagesAsync(documentId, pages.Count, ct);

            PartsInvoiceHeader? header = null;     // first page with a header wins (supplier/number/date/currency)
            decimal? totalAmount = null;           // last page with a total wins
            var allLines = new List<(int pageNo, PartsInvoiceLine line)>();
            var rawByPage = new Dictionary<int, PartsInvoicePageExtraction>();
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

                    header ??= result.Header;
                    if (result.Header?.TotalAmount is not null) totalAmount = result.Header.TotalAmount;

                    foreach (var line in result.Lines)
                        allLines.Add((pageNo, line));

                    sw.Stop();
                    await _docs.RecordPageCompletedAsync(documentId, pageNo, sw.Elapsed.TotalSeconds, ct);
                    successfulPages++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sw.Stop();
                    _logger.LogError(ex, "Parts page {Page} extraction failed for document {Id}", pageNo, documentId);
                    failedPages.Add((pageNo, ex.Message));
                    await _docs.RecordPageCompletedAsync(documentId, pageNo, sw.Elapsed.TotalSeconds, ct);
                }
            }

            if (successfulPages == 0)
            {
                var allFailedMsg = $"All {pages.Count} page(s) failed extraction. First error: {failedPages[0].Error}";
                _logger.LogError("Parts document {Id}: {Message}", documentId, allFailedMsg);
                await _docs.UpdateStatusAsync(documentId, "failed", allFailedMsg, ct);
                return;
            }

            // Persist lines (replace prior → idempotent re-extract), renumbering globally across pages.
            await _lines.DeleteByDocumentAsync(documentId, ct);
            var rows = allLines.Select((t, idx) => MapToRow(documentId, idx + 1, t.pageNo, t.line)).ToList();
            await _lines.InsertManyAsync(documentId, rows, ct);

            var (validationStatus, validationNotes) =
                _validator.Validate(allLines.Select(t => t.line).ToList(), header, totalAmount);

            // Combine validation notes + any partial-page warning into ErrorMessage (no ValidationNotes column).
            var msgs = new List<string>();
            if (!string.IsNullOrWhiteSpace(validationNotes)) msgs.Add(validationNotes!);
            if (failedPages.Count > 0)
                msgs.Add($"{failedPages.Count}/{pages.Count} page(s) failed: " +
                         string.Join("; ", failedPages.Select(p => $"page {p.PageNo}: {p.Error}")));
            var errorMessage = msgs.Count > 0 ? string.Join(" | ", msgs) : null;

            var rawJson = JsonSerializer.Serialize(rawByPage);
            await _docs.UpdateAfterExtractionAsync(
                documentId,
                pageCount: pages.Count,
                header: ToHeaderUpdate(header, totalAmount),
                validationStatus: validationStatus,
                errorMessage: errorMessage,
                rawExtractionJson: rawJson,
                ct: ct);

            _logger.LogInformation(
                "Parts document {Id}: extraction complete — {Lines} line(s), {Ok}/{Total} page(s) ok, validation={Validation}.",
                documentId, rows.Count, successfulPages, pages.Count, validationStatus);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Parts extraction failed for document {Id}", documentId);
            try { await _docs.UpdateStatusAsync(documentId, "failed", ex.Message, ct); }
            catch (Exception inner) { _logger.LogError(inner, "Also failed to mark parts document {Id} failed.", documentId); }
        }
    }

    private static StagingPartsLineRow MapToRow(Guid docId, int lineNo, int pageNo, PartsInvoiceLine line)
        => new(
            Id:                    Guid.NewGuid(),
            DocumentId:            docId,
            LineNumber:            lineNo,
            PageNumber:            pageNo,
            SupplierArticleNumber: line.SupplierArticleNumber,
            OemNumbers:            CleanOems(line.OemNumbers),
            Description:           line.Description,
            Brand:                 line.Brand,
            Quantity:              line.Quantity,
            Unit:                  line.Unit,
            UnitPriceForeign:      line.UnitPriceForeign,
            DiscountPct:           line.DiscountPct,
            LineTotalForeign:      line.LineTotalForeign,
            IsPromotional:         PartsPromotionRules.IsPromotional(line));

    private static List<string> CleanOems(List<string>? oems) =>
        oems is null ? new List<string>()
                     : oems.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList();

    private static PartsHeaderUpdate? ToHeaderUpdate(PartsInvoiceHeader? h, decimal? totalAmount)
        => (h is null && totalAmount is null) ? null : new PartsHeaderUpdate(
            SupplierName:  h?.SupplierName,
            InvoiceNumber: h?.InvoiceNumber,
            InvoiceDate:   ParseDate(h?.InvoiceDate),
            Currency:      h?.Currency,
            TotalAmount:   totalAmount);

    private static DateTime? ParseDate(string? s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
}
