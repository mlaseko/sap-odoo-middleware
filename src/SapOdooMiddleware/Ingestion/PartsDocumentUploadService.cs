using System.Security.Cryptography;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// Autohub upload path (in-process; used by the API controller and the Razor upload page).
/// Deliberately parallel to the Lubes <c>DocumentUploadService</c> rather than refactoring it, so
/// the Lubes runtime path stays byte-for-byte unchanged (unification is a follow-up). Validates,
/// SHA256-dedupes within the Autohub DB, stores the PDF under the Autohub storage root, inserts the
/// parts staging row, and enqueues parts extraction. Tenant config is resolved via ICompanyContext.
/// </summary>
public class PartsDocumentUploadService
{
    private readonly IStagingPartsDocumentRepository _docs;
    private readonly IPartsExtractionQueue _queue;
    private readonly ICompanyContext _company;
    private readonly ILogger<PartsDocumentUploadService> _logger;

    public PartsDocumentUploadService(
        IStagingPartsDocumentRepository docs,
        IPartsExtractionQueue queue,
        ICompanyContext company,
        ILogger<PartsDocumentUploadService> logger)
    {
        _docs = docs;
        _queue = queue;
        _company = company;
        _logger = logger;
    }

    public async Task<DocumentUploadResult> SaveAndQueueAsync(IFormFile? file, CancellationToken ct)
    {
        var ingestion = _company.Current.DocumentIngestion;

        if (file is null || file.Length == 0)
            return DocumentUploadResult.Fail("No file uploaded.");
        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return DocumentUploadResult.Fail("Only PDF files are accepted.");
        if (file.Length > ingestion.MaxUploadMb * 1024L * 1024L)
            return DocumentUploadResult.Fail($"File exceeds the {ingestion.MaxUploadMb} MB limit.");

        string hash;
        await using (var s = file.OpenReadStream())
        {
            using var sha = SHA256.Create();
            var hashBytes = await sha.ComputeHashAsync(s, ct);
            hash = Convert.ToHexString(hashBytes);
        }

        var existing = await _docs.GetBySha256Async(hash, ct);
        if (existing is not null)
        {
            _logger.LogInformation("Autohub upload deduplicated: {File} → existing document {Id}.", file.FileName, existing.Id);
            return DocumentUploadResult.Success(existing.Id, existing.Status, deduplicated: true);
        }

        var documentId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var subdir = Path.Combine(ingestion.StorageRoot, now.ToString("yyyy"), now.ToString("MM"), documentId.ToString());
        Directory.CreateDirectory(subdir);
        var filePath = Path.Combine(subdir, file.FileName);
        await using (var dst = File.Create(filePath))
            await file.CopyToAsync(dst, ct);

        await _docs.CreateAsync(documentId, file.FileName, filePath, hash, ct);
        _queue.Enqueue(documentId);

        _logger.LogInformation("Uploaded Autohub document {Id} ({File}); extraction queued.", documentId, file.FileName);
        return DocumentUploadResult.Success(documentId, "uploaded", deduplicated: false);
    }
}
