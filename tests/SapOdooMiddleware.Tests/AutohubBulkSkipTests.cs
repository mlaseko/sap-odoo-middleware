using Microsoft.AspNetCore.Mvc;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Tests;

/// <summary>Bulk-skip endpoint: guards on document existence and returns the affected-line count.</summary>
public class AutohubBulkSkipTests
{
    private static StagingPartsDocumentRow Doc(Guid id) => new(
        Id: id, OriginalFilename: "inv.pdf", FilePath: "/tmp/inv.pdf", FileSha256: "sha", PageCount: 1,
        Status: "extracted", ValidationStatus: "ok", ErrorMessage: null, UploadedAt: DateTime.UtcNow,
        ExtractedAt: DateTime.UtcNow, PagesProcessed: 1, CurrentPageStartedAt: null, LastPageDurationSec: null,
        SupplierName: null, InvoiceNumber: null, InvoiceDate: null, Currency: "USD", TotalAmount: null);

    // Only _docs and _review are exercised by bulk-skip; the rest (incl. the Autohub SAP service and the
    // Neon bridge) are not invoked.
    private static AutohubDocumentsController Build(Mock<IStagingPartsDocumentRepository> docs, Mock<IPartsReviewRepository> review)
        => new(docs.Object, review.Object, null!, null!, null!, null!, null!, null!, null!, null!);

    [Fact]
    public async Task BulkSkipPending_DocExists_SkipsAndReturnsCount()
    {
        var docId = Guid.NewGuid();
        var docs = new Mock<IStagingPartsDocumentRepository>();
        docs.Setup(d => d.GetByIdAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(Doc(docId));
        var review = new Mock<IPartsReviewRepository>();
        review.Setup(r => r.BulkSkipPendingAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(24);

        var result = await Build(docs, review).BulkSkipPending(docId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(24, ok.Value!.GetType().GetProperty("skipped")!.GetValue(ok.Value));
        review.Verify(r => r.BulkSkipPendingAsync(docId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BulkSkipPending_DocMissing_NotFound_NoSkip()
    {
        var docId = Guid.NewGuid();
        var docs = new Mock<IStagingPartsDocumentRepository>();
        docs.Setup(d => d.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((StagingPartsDocumentRow?)null);
        var review = new Mock<IPartsReviewRepository>();

        var result = await Build(docs, review).BulkSkipPending(docId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        review.Verify(r => r.BulkSkipPendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BulkReopenSkipped_NeedsManual_CallsReopen()
    {
        var docId = Guid.NewGuid();
        var docs = new Mock<IStagingPartsDocumentRepository>();
        docs.Setup(d => d.GetByIdAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(Doc(docId));
        var review = new Mock<IPartsReviewRepository>();
        review.Setup(r => r.BulkReopenSkippedAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(24);

        var result = await Build(docs, review).BulkReopenSkipped(docId, reEnrich: false, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(24, ok.Value!.GetType().GetProperty("reopened")!.GetValue(ok.Value));
        review.Verify(r => r.BulkReopenSkippedAsync(docId, It.IsAny<CancellationToken>()), Times.Once);
        review.Verify(r => r.BulkReenrichSkippedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BulkReopenSkipped_ReEnrich_CallsReenrich()
    {
        var docId = Guid.NewGuid();
        var docs = new Mock<IStagingPartsDocumentRepository>();
        docs.Setup(d => d.GetByIdAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(Doc(docId));
        var review = new Mock<IPartsReviewRepository>();
        review.Setup(r => r.BulkReenrichSkippedAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(22);

        var result = await Build(docs, review).BulkReopenSkipped(docId, reEnrich: true, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(22, ok.Value!.GetType().GetProperty("reopened")!.GetValue(ok.Value));
        review.Verify(r => r.BulkReenrichSkippedAsync(docId, It.IsAny<CancellationToken>()), Times.Once);
        review.Verify(r => r.BulkReopenSkippedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_DocExists_DeletesAndReturnsOk()
    {
        var docId = Guid.NewGuid();
        var docs = new Mock<IStagingPartsDocumentRepository>();
        docs.Setup(d => d.GetByIdAsync(docId, It.IsAny<CancellationToken>())).ReturnsAsync(Doc(docId));
        var review = new Mock<IPartsReviewRepository>();

        var result = await Build(docs, review).Delete(docId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        docs.Verify(d => d.DeleteAsync(docId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_DocMissing_NotFound_NoDelete()
    {
        var docs = new Mock<IStagingPartsDocumentRepository>();
        docs.Setup(d => d.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((StagingPartsDocumentRow?)null);
        var review = new Mock<IPartsReviewRepository>();

        var result = await Build(docs, review).Delete(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        docs.Verify(d => d.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
