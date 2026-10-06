using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Tests;

/// <summary>Lubes DELETE /api/documents/{id}: existence guards and best-effort stored-file cleanup.</summary>
public class LubesDocumentDeleteTests
{
    private static StagingDocumentRow Doc(Guid id, string filePath) => new(
        Id: id, OriginalFilename: "inv.pdf", FilePath: filePath, FileHash: "hash", FileSizeBytes: 1,
        PageCount: 1, Status: "failed", DocumentType: "invoice", Supplier: null, InvoiceNumber: null,
        InvoiceDate: null, SalesOrder: null, DeliveryNoteRef: null, CustomerName: null, CustomerAccount: null,
        Currency: null, Subtotal: null, Freight: null, TotalNet: null, TaxAmount: null, InvoiceTotal: null,
        PaymentTerms: null, DueDate: null, ValidationStatus: null, ValidationNotes: null, ErrorMessage: null,
        UploadedAt: DateTime.UtcNow, ExtractedAt: null, PagesProcessed: 0, CurrentPageStartedAt: null,
        LastPageDurationSec: null, ReviewedAt: null, ReviewedBy: null, AutoMatchedAt: null, AutoMatchedCount: 0);

    // Only the document repository is exercised by delete; the other dependencies are not invoked.
    private static DocumentsController Build(Mock<IStagingDocumentRepository> docs) =>
        new(docs.Object, new Mock<IStagingDocumentLineRepository>().Object, null!, null!, null!, null!,
            NullLogger<DocumentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static Mock<IStagingDocumentRepository> Repo(StagingDocumentRow? doc, bool deleted)
    {
        var docs = new Mock<IStagingDocumentRepository>();
        docs.Setup(d => d.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(doc);
        docs.Setup(d => d.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(deleted);
        return docs;
    }

    [Fact]
    public async Task Delete_DocExists_DeletesAndReturnsOk()
    {
        var id = Guid.NewGuid();
        var docs = Repo(Doc(id, Path.Combine(Path.GetTempPath(), "missing-" + id, "inv.pdf")), deleted: true);

        var result = await Build(docs).Delete(id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        docs.Verify(d => d.DeleteAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_DocMissing_NotFound_NoDelete()
    {
        var docs = Repo(null, deleted: false);

        var result = await Build(docs).Delete(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        docs.Verify(d => d.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_RemovedConcurrently_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        var docs = Repo(Doc(id, Path.Combine(Path.GetTempPath(), "missing-" + id, "inv.pdf")), deleted: false);

        var result = await Build(docs).Delete(id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_RemovesStoredFileAndEmptyDocumentFolder_KeepsParent()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "lubes-del-" + Guid.NewGuid());
        var month = Path.Combine(root, "2026", "10");
        var docDir = Path.Combine(month, id.ToString());
        Directory.CreateDirectory(docDir);
        var pdf = Path.Combine(docDir, "inv.pdf");
        File.WriteAllText(pdf, "pdf");
        var sibling = Path.Combine(month, "other.txt");   // another document's data must survive
        File.WriteAllText(sibling, "keep");
        try
        {
            var result = await Build(Repo(Doc(id, pdf), deleted: true)).Delete(id, CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
            Assert.False(File.Exists(pdf));
            Assert.False(Directory.Exists(docDir));
            Assert.True(Directory.Exists(month));
            Assert.True(File.Exists(sibling));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Delete_FolderNotNamedForDocument_KeepsFolder()
    {
        var id = Guid.NewGuid();
        var dir = Path.Combine(Path.GetTempPath(), "lubes-del-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var pdf = Path.Combine(dir, "inv.pdf");
        File.WriteAllText(pdf, "pdf");
        try
        {
            var result = await Build(Repo(Doc(id, pdf), deleted: true)).Delete(id, CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
            Assert.False(File.Exists(pdf));
            Assert.True(Directory.Exists(dir));   // not the {documentId} leaf, so never removed
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
