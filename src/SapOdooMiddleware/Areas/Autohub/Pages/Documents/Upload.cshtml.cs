using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Ingestion;
using SapOdooMiddleware.Services.Autohub.Excel;
using SapOdooMiddleware.Services.Autohub.Excel.Models;

namespace SapOdooMiddleware.Areas.Autohub.Pages.Documents;

[RequestSizeLimit(50 * 1024 * 1024)]   // 50 MB, matching MaxUploadMb
public class UploadModel : PageModel
{
    private readonly PartsDocumentUploadService _uploads;
    private readonly ExcelTemplateGenerator _excelTemplate;
    private readonly ExcelUploadHandler _excelUpload;

    public UploadModel(
        PartsDocumentUploadService uploads,
        ExcelTemplateGenerator excelTemplate,
        ExcelUploadHandler excelUpload)
    {
        _uploads = uploads;
        _excelTemplate = excelTemplate;
        _excelUpload = excelUpload;
    }

    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [BindProperty]
    public IFormFile? Pdf { get; set; }

    [BindProperty]
    public IFormFile? Excel { get; set; }

    public string? Error { get; private set; }

    /// <summary>Hard per-cell errors from an Excel import (shown as a list when present).</summary>
    public IReadOnlyList<ExcelParseError>? ExcelErrors { get; private set; }

    /// <summary>Set when an Excel upload was a duplicate, so the page can link to the existing document.</summary>
    public Guid? ExistingDocumentId { get; private set; }

    public void OnGet() => ViewData["Title"] = "Upload";

    /// <summary>Download the blank Excel template (no API key — served from the page).</summary>
    public IActionResult OnGetExcelTemplate()
        => File(_excelTemplate.Generate(), XlsxContentType, ExcelTemplateSchema.TemplateFileName);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        ViewData["Title"] = "Upload";

        var result = await _uploads.SaveAndQueueAsync(Pdf, ct);
        if (!result.Ok)
        {
            Error = result.Error;
            return Page();
        }

        if (result.Deduplicated)
            TempData["Notice"] = "Document already uploaded — opening the existing record.";

        return Redirect($"/autohub/documents/{result.DocumentId}");
    }

    public async Task<IActionResult> OnPostExcelAsync(CancellationToken ct)
    {
        ViewData["Title"] = "Upload";

        var result = await _excelUpload.SaveAndCreateAsync(Excel, ct);
        switch (result.Outcome)
        {
            case ExcelUploadOutcome.Created:
                if (result.Warnings.Count > 0)
                    TempData["Notice"] = $"Imported {result.LinesCreated} line(s) — {result.Warnings.Count} auto-corrected on import (see the validation banner).";
                else
                    TempData["Notice"] = $"Imported {result.LinesCreated} line(s) from Excel.";
                return Redirect($"/autohub/documents/{result.DocumentId}");

            case ExcelUploadOutcome.Duplicate:
                Error = "This workbook was already uploaded.";
                ExistingDocumentId = result.DocumentId;
                return Page();

            case ExcelUploadOutcome.HardFailure:
                Error = "The workbook could not be imported — fix the issues below and re-upload.";
                ExcelErrors = result.HardErrors;
                return Page();

            default:
                Error = result.Error;
                return Page();
        }
    }
}
