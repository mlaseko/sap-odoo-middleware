using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Services.Autohub.Excel;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Autohub Excel invoice path: download the blank template and import a filled-in workbook. The import
/// produces the SAME staging rows as the PDF path (Status=extracted, lines pending) so AutoMatchWorker,
/// enrichment and the review UI treat it identically — just without the vision/DGX step. JSON API for
/// programmatic clients; the operator UI uses the equivalent Razor page handlers (no API key).
/// </summary>
[ApiController]
[Route("api/autohub/documents")]
public sealed class AutohubExcelController : ControllerBase
{
    private readonly ExcelTemplateGenerator _template;
    private readonly ExcelUploadHandler _upload;

    public AutohubExcelController(ExcelTemplateGenerator template, ExcelUploadHandler upload)
    {
        _template = template;
        _upload = upload;
    }

    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Download the blank Excel template (dynamically generated, dropdowns included).</summary>
    [HttpGet("excel-template")]
    public IActionResult ExcelTemplate()
        => File(_template.Generate(), XlsxContentType, ExcelTemplateSchema.TemplateFileName);

    /// <summary>Import a filled-in workbook. 201 created (+warnings/sigma), 400 hard-fail, 409 duplicate.</summary>
    [HttpPost("upload-excel")]
    [RequestSizeLimit(50 * 1024 * 1024)]   // 50 MB, matching the PDF path
    public async Task<IActionResult> UploadExcel(IFormFile file, CancellationToken ct)
    {
        var r = await _upload.SaveAndCreateAsync(file, ct);
        return r.Outcome switch
        {
            ExcelUploadOutcome.Created => StatusCode(StatusCodes.Status201Created, new
            {
                documentId = r.DocumentId,
                linesCreated = r.LinesCreated,
                validationWarnings = r.Warnings.Select(w => new { row = w.Row, field = w.Field, issue = w.Issue }),
                sigma = new
                {
                    sumLineTotals = r.Sigma!.SumLineTotals,
                    invoiceTotal = r.Sigma.InvoiceTotal,
                    deltaPct = r.Sigma.DeltaPct,
                },
            }),
            ExcelUploadOutcome.Duplicate => Conflict(new { error = "duplicate file (already uploaded)", documentId = r.DocumentId }),
            ExcelUploadOutcome.HardFailure => BadRequest(new
            {
                error = "validation failed",
                errors = r.HardErrors.Select(e => new { row = e.Row, field = e.Field, issue = e.Issue }),
            }),
            _ => BadRequest(new { error = r.Error }),
        };
    }
}
