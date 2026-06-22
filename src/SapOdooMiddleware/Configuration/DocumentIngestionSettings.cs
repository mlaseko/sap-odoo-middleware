namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Settings for invoice/document ingestion (Phase A). PDF bytes live on disk under
/// <see cref="StorageRoot"/>; only paths/metadata are stored in Neon.
/// </summary>
public class DocumentIngestionSettings
{
    public const string SectionName = "DocumentIngestion";

    /// <summary>Root folder where uploaded PDFs are stored. Subfolders created per yyyy/MM/document-id.</summary>
    public string StorageRoot { get; set; } = @"C:\SapOdoo\Documents";

    /// <summary>Maximum upload size in megabytes.</summary>
    public int MaxUploadMb { get; set; } = 50;
}
