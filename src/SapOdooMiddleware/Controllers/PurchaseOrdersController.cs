using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.ItemProvisioning;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Purchase Order creation from a reviewed invoice. Preview is built server-side from the invoice
/// (vendor, currency, NumAtCard, comments, sellable lines); the user edits lines and posts.
/// </summary>
[ApiController]
[Route("api/documents/{documentId:guid}/purchase-order")]
public class PurchaseOrdersController : ControllerBase
{
    private readonly IStagingDocumentRepository _docs;
    private readonly PurchaseOrderService _po;
    private readonly ILogger<PurchaseOrdersController> _logger;

    public PurchaseOrdersController(
        IStagingDocumentRepository docs,
        PurchaseOrderService po,
        ILogger<PurchaseOrdersController> logger)
    {
        _docs = docs;
        _po = po;
        _logger = logger;
    }

    /// <summary>Proposed PO for the invoice — vendor, currency, lines, and readiness/blocking reasons.</summary>
    [HttpGet("preview")]
    public async Task<IActionResult> Preview(Guid documentId, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();
        return Ok(await _po.BuildPreviewAsync(doc, ct));
    }

    /// <summary>Post the (user-edited) PO to SAP B1. Body carries the final lines only.</summary>
    [HttpPost]
    public async Task<IActionResult> Create(Guid documentId, [FromBody] CreatePoRequest body, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();

        var lines = (body.Lines ?? new())
            .Select(l => new PoPostLine(l.ItemCode?.Trim() ?? "", l.Quantity, l.UnitPrice, l.DiscountPct))
            .ToList();

        var result = await _po.PostAsync(doc, lines, ct);

        if (result.AlreadyExists)
            return Conflict(new { error = result.Error, docEntry = result.DocEntry, docNum = result.DocNum });
        if (!result.Ok)
            return BadRequest(new { error = result.Error });

        // Close the document so its PO can't be posted twice from the UI. SAP-side dedup (NumAtCard)
        // remains the hard guard; this just reflects the closed state. A failure here must not fail the
        // response — the PO is already created in SAP.
        try
        {
            await _docs.UpdateStatusAsync(documentId, "completed", null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "PO {DocNum} created for document {Id} but marking it 'completed' (closed) failed.",
                result.DocNum, documentId);
        }

        return Ok(new { docEntry = result.DocEntry, docNum = result.DocNum, numAtCard = doc.InvoiceNumber });
    }
}

public record CreatePoRequest(List<CreatePoLineRequest>? Lines);
public record CreatePoLineRequest(string? ItemCode, double Quantity, double UnitPrice, double DiscountPct);
