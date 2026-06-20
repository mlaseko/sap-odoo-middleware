using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Ingestion;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub.Excel.Models;
using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Services.Autohub.Excel;

public enum ExcelUploadOutcome { Created, Duplicate, HardFailure, BadRequest }

public sealed record ExcelLineWarning(int Row, string Field, string Issue);

public sealed record ExcelSigmaResult(decimal SumLineTotals, decimal? InvoiceTotal, decimal DeltaPct);

/// <summary>Result of an Excel import. Maps cleanly onto the HTTP 201 / 400 / 409 responses.</summary>
public sealed record ExcelUploadResult(
    ExcelUploadOutcome Outcome,
    Guid? DocumentId,
    int LinesCreated,
    IReadOnlyList<ExcelLineWarning> Warnings,
    ExcelSigmaResult? Sigma,
    IReadOnlyList<ExcelParseError> HardErrors,
    string? Error)
{
    private static readonly IReadOnlyList<ExcelLineWarning> NoWarnings = Array.Empty<ExcelLineWarning>();
    private static readonly IReadOnlyList<ExcelParseError> NoErrors = Array.Empty<ExcelParseError>();

    public static ExcelUploadResult Bad(string error) =>
        new(ExcelUploadOutcome.BadRequest, null, 0, NoWarnings, null, NoErrors, error);
    public static ExcelUploadResult HardFail(IReadOnlyList<ExcelParseError> errors) =>
        new(ExcelUploadOutcome.HardFailure, null, 0, NoWarnings, null, errors, "validation failed");
    public static ExcelUploadResult Dup(Guid existingId) =>
        new(ExcelUploadOutcome.Duplicate, existingId, 0, NoWarnings, null, NoErrors, "duplicate file (already uploaded)");
}

/// <summary>
/// Orchestrates the Excel invoice import: validate the file, parse it, run the shared per-line
/// validator, persist the SAME <c>staging_document</c> + <c>staging_document_line</c> rows the PDF path
/// produces (Status=extracted, lines ReviewStatus=pending), and report warnings + sigma. SHA256 dedup
/// mirrors the PDF uploader. No vision/DGX involvement — sub-second, deterministic.
/// </summary>
public sealed class ExcelUploadHandler
{
    private readonly IStagingPartsDocumentRepository _docs;
    private readonly IStagingPartsLineRepository _lines;
    private readonly ExcelInvoiceParser _parser;
    private readonly ILineValidator _validator;
    private readonly PartsInvoiceValidator _docValidator;
    private readonly ICompanyContext _company;
    private readonly ILogger<ExcelUploadHandler> _logger;

    public ExcelUploadHandler(
        IStagingPartsDocumentRepository docs,
        IStagingPartsLineRepository lines,
        ExcelInvoiceParser parser,
        ILineValidator validator,
        PartsInvoiceValidator docValidator,
        ICompanyContext company,
        ILogger<ExcelUploadHandler> logger)
    {
        _docs = docs;
        _lines = lines;
        _parser = parser;
        _validator = validator;
        _docValidator = docValidator;
        _company = company;
        _logger = logger;
    }

    public async Task<ExcelUploadResult> SaveAndCreateAsync(IFormFile? file, CancellationToken ct)
    {
        var ingestion = _company.Current.DocumentIngestion;

        if (file is null || file.Length == 0)
            return ExcelUploadResult.Bad("No file uploaded.");
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return ExcelUploadResult.Bad("Only .xlsx files are accepted.");
        if (file.Length > ingestion.MaxUploadMb * 1024L * 1024L)
            return ExcelUploadResult.Bad($"File exceeds the {ingestion.MaxUploadMb} MB limit.");

        // Buffer once — we need the bytes for both the hash and the parse, and again to store on disk.
        byte[] bytes;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var existing = await _docs.GetBySha256Async(hash, ct);
        if (existing is not null)
        {
            _logger.LogInformation("Autohub Excel upload deduplicated: {File} → existing document {Id}.", file.FileName, existing.Id);
            return ExcelUploadResult.Dup(existing.Id);
        }

        ExcelParseResult parsed;
        using (var ps = new MemoryStream(bytes, writable: false))
            parsed = _parser.Parse(ps);
        if (!parsed.Ok)
        {
            _logger.LogInformation("Autohub Excel upload rejected ({File}): {Count} hard error(s).", file.FileName, parsed.HardErrors.Count);
            return ExcelUploadResult.HardFail(parsed.HardErrors);
        }
        var doc = parsed.Document!;

        // Shared per-line sanitisation (same rules the PDF path applies to the DGX response).
        var warnings = new List<ExcelLineWarning>();
        var sanitised = new List<(PartsInvoiceLine Line, bool Promo)>(doc.Lines.Count);
        foreach (var pl in doc.Lines)
        {
            var vr = _validator.Validate(pl.Line);
            foreach (var issue in vr.Issues)
                warnings.Add(new ExcelLineWarning(pl.ExcelRow, issue.Field, issue.Code));
            sanitised.Add((vr.Line, pl.ExplicitPromotional));
        }

        var sanitisedLines = sanitised.Select(s => s.Line).ToList();
        var (validationStatus, validationNotes) = _docValidator.Validate(sanitisedLines, doc.Header, doc.Header.TotalAmount);

        var sumLineTotals = sanitisedLines.Sum(l => l.LineTotalForeign ?? 0m);
        var total = doc.Header.TotalAmount;
        var deltaPct = total is { } tt && tt != 0m ? Math.Abs(sumLineTotals - tt) / Math.Abs(tt) * 100m : 0m;
        var sigma = new ExcelSigmaResult(sumLineTotals, total, decimal.Round(deltaPct, 2));

        // ---- Persist (identical two-step shape to the PDF path). ----
        var documentId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var subdir = Path.Combine(ingestion.StorageRoot, now.ToString("yyyy"), now.ToString("MM"), documentId.ToString());
        Directory.CreateDirectory(subdir);
        var filePath = Path.Combine(subdir, file.FileName);
        await File.WriteAllBytesAsync(filePath, bytes, ct);

        await _docs.CreateAsync(documentId, file.FileName, filePath, hash, ct);

        var rows = sanitised.Select((s, idx) => new StagingPartsLineRow(
            Id:                    Guid.NewGuid(),
            DocumentId:            documentId,
            LineNumber:            idx + 1,
            PageNumber:            1,
            SupplierArticleNumber: s.Line.SupplierArticleNumber,
            OemNumbers:            s.Line.OemNumbers ?? new List<string>(),
            Description:           s.Line.Description,
            Brand:                 s.Line.Brand,
            Quantity:              s.Line.Quantity,
            Unit:                  s.Line.Unit,
            UnitPriceForeign:      s.Line.UnitPriceForeign,
            DiscountPct:           s.Line.DiscountPct,
            LineTotalForeign:      s.Line.LineTotalForeign,
            IsPromotional:         s.Promo || PartsPromotionRules.IsPromotional(s.Line))).ToList();
        await _lines.InsertManyAsync(documentId, rows, ct);

        var rawJson = JsonSerializer.Serialize(new
        {
            source = "excel",
            originalFilename = file.FileName,
            header = doc.Header,
            forexRateUsed = doc.ForexRateUsed,
            forexRateDate = doc.ForexRateDate,
            warnings,
            lines = doc.Lines.Select(l => new { row = l.ExcelRow, line = l.Line, explicitPromotional = l.ExplicitPromotional }),
        });

        var errorMessage = BuildErrorMessage(validationNotes, warnings);
        var headerUpdate = new PartsHeaderUpdate(
            doc.Header.SupplierName, doc.Header.InvoiceNumber,
            ParseIsoDate(doc.Header.InvoiceDate), doc.Header.Currency, doc.Header.TotalAmount);
        await _docs.UpdateAfterExtractionAsync(documentId, pageCount: 1, headerUpdate, validationStatus, errorMessage, rawJson, ct);

        _logger.LogInformation(
            "Autohub Excel import {Id} ({File}): {Lines} line(s), {Warnings} warning(s), validation={Validation}, sigma Δ={Delta:N2}%.",
            documentId, file.FileName, rows.Count, warnings.Count, validationStatus, sigma.DeltaPct);

        return new ExcelUploadResult(ExcelUploadOutcome.Created, documentId, rows.Count, warnings, sigma,
            Array.Empty<ExcelParseError>(), null);
    }

    private static string? BuildErrorMessage(string? validationNotes, IReadOnlyList<ExcelLineWarning> warnings)
    {
        var msgs = new List<string>();
        if (!string.IsNullOrWhiteSpace(validationNotes)) msgs.Add(validationNotes!);
        if (warnings.Count > 0)
        {
            var histogram = warnings
                .GroupBy(w => w.Issue)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}: {g.Count()}");
            msgs.Add($"{warnings.Count} line warning(s) auto-corrected on import ({string.Join(", ", histogram)}).");
        }
        return msgs.Count > 0 ? string.Join(" | ", msgs) : null;
    }

    private static DateTime? ParseIsoDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
}
