namespace SapOdooMiddleware.Pages;

/// <summary>
/// View model for the shared <c>_ExtractionProgress</c> partial: the live progress card + poller.
/// <paramref name="StatusUrl"/> is the document's status endpoint (tenant-specific), so the same
/// partial drives both the Lubes and Autohub Detail pages.
/// </summary>
public record ExtractionProgressVm(Guid Id, string Status, string StatusUrl);
