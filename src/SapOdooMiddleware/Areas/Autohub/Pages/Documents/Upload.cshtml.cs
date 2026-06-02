using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Ingestion;

namespace SapOdooMiddleware.Areas.Autohub.Pages.Documents;

[RequestSizeLimit(50 * 1024 * 1024)]   // 50 MB, matching MaxUploadMb
public class UploadModel : PageModel
{
    private readonly PartsDocumentUploadService _uploads;
    public UploadModel(PartsDocumentUploadService uploads) => _uploads = uploads;

    [BindProperty]
    public IFormFile? Pdf { get; set; }

    public string? Error { get; private set; }

    public void OnGet() => ViewData["Title"] = "Upload";

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
}
