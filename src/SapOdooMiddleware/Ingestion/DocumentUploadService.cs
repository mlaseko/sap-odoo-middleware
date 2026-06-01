using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// Shared upload path used by both the API controller and the Razor upload page (in-process,
/// no self-HTTP). Validates, SHA256-dedupes, stores the PDF on disk under
/// StorageRoot/yyyy/MM/{documentId}/, inserts the staging_document row, and enqueues extraction.
/// </summary>
public class DocumentUploadService
{
    private readonly IStagingDocumentRepository _docs;
    private readonly IDocumentExtractionQueue   _queue;
    private readonly DocumentIngestionSettings  _settings;
    private readonly ILogger<DocumentUploadService> _logger;

    public DocumentUploadService(
        IStagingDocumentRepository docs,
        IDocumentExtractionQueue queue,
        IOptions<DocumentIngestionSettings> settings,
        ILogger<DocumentUploadService> logger)
    {
        _docs     = docs;
        _queue    = queue;
        _settings = settings.Value;
        _logger   = logger;
    }

    public async Task<DocumentUploadResult> SaveAndQueueAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return DocumentUploadResult.Fail("No file uploaded.");
        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return DocumentUploadResult.Fail("Only PDF files are accepted.");
        if (file.Length > _settings.MaxUploadMb * 1024L * 1024L)
            return DocumentUploadResult.Fail($"File exceeds the {_settings.MaxUploadMb} MB limit.");

        // Hash to dedupe.
        string hash;
        await using (var s = file.OpenReadStream())
        {
            using var sha = SHA256.Create();
            var hashBytes = await sha.ComputeHashAsync(s, ct);
            hash = Convert.ToHexString(hashBytes);
        }

        var existing = await _docs.GetByHashAsync(hash, ct);
        if (existing is not null)
        {
            _logger.LogInformation("Upload deduplicated: {File} → existing document {Id}.", file.FileName, existing.Id);
            return DocumentUploadResult.Success(existing.Id, existing.Status, deduplicated: true);
        }

        // Save to disk under StorageRoot/yyyy/MM/{documentId}/{filename}.
        var documentId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var subdir = Path.Combine(_settings.StorageRoot, now.ToString("yyyy"), now.ToString("MM"), documentId.ToString());
        Directory.CreateDirectory(subdir);
        var filePath = Path.Combine(subdir, file.FileName);
        await using (var dst = File.Create(filePath))
            await file.CopyToAsync(dst, ct);

        await _docs.CreateAsync(documentId, file.FileName, filePath, hash, file.Length, ct);
        _queue.Enqueue(documentId);

        _logger.LogInformation("Uploaded document {Id} ({File}); extraction queued.", documentId, file.FileName);
        return DocumentUploadResult.Success(documentId, "uploaded", deduplicated: false);
    }
}

public record DocumentUploadResult(bool Ok, string? Error, Guid? DocumentId, string? Status, bool Deduplicated)
{
    public static DocumentUploadResult Fail(string error) => new(false, error, null, null, false);
    public static DocumentUploadResult Success(Guid id, string status, bool deduplicated) =>
        new(true, null, id, status, deduplicated);
}
