using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Inventory;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Purchase order from a reviewed Autohub invoice (the Create &amp; finish tab), and the SAP vendor picker.
/// Posts into the Autohub SAP company. Requires the <c>X-Api-Key</c> header.
/// </summary>
[ApiController]
[Route("api/autohub")]
public sealed class AutohubPurchaseOrderController : ControllerBase
{
    private readonly IAutohubPurchaseOrderService _po;
    private readonly IAutohubInventorySqlService _sql;

    public AutohubPurchaseOrderController(IAutohubPurchaseOrderService po, IAutohubInventorySqlService sql)
    {
        _po = po;
        _sql = sql;
    }

    private string CurrentUser => HttpContext.User?.Identity?.Name is { Length: > 0 } n ? n : "operator";

    /// <summary>
    /// GET /api/autohub/documents/{documentId}/purchase-order[?card_code=VEN006]
    /// What "Create purchase order" would post: vendor (the one given, else the one remembered for the
    /// supplier), warehouse, lines at PL01 in TZS, lines left off and why, total, what blocks it, and the
    /// purchase order already created from this invoice, if any. Read-only.
    /// </summary>
    [HttpGet("documents/{documentId:guid}/purchase-order")]
    [ProducesResponseType(typeof(AutohubPoPreview), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Preview(
        Guid documentId, [FromQuery(Name = "card_code")] string? cardCode, CancellationToken ct)
    {
        var preview = await _po.PreviewAsync(documentId, cardCode, ct);
        return preview is null ? NotFound(new { error = "Invoice not found." }) : Ok(preview);
    }

    /// <summary>
    /// POST /api/autohub/documents/{documentId}/purchase-order  body: <c>{ "card_code": "VEN006" }</c>
    /// Creates the SAP purchase order from the reviewed invoice. 200 with the PO (also when it already
    /// existed: <c>already_existed</c>); 400 when something blocks it (the reasons); 404 no invoice; 409 the
    /// vendor already has a PO for this invoice number; 422 the warehouse has no active branch; 500 SAP refused.
    /// </summary>
    [HttpPost("documents/{documentId:guid}/purchase-order")]
    public async Task<IActionResult> Create(Guid documentId, [FromBody] CreatePurchaseOrderBody? body, CancellationToken ct)
    {
        var result = await _po.CreateAsync(documentId, body?.CardCode ?? "", CurrentUser, ct);
        if (result.Status == 200)
            return Ok(new { purchase_order = result.PurchaseOrder, already_existed = result.AlreadyExisted });
        return StatusCode(result.Status, new { error = string.Join(" ", result.Errors), errors = result.Errors });
    }

    /// <summary>GET /api/autohub/vendors?search=vika&amp;limit=50 — SAP vendors (OCRD CardType 'S', active) for the picker.</summary>
    [HttpGet("vendors")]
    [ProducesResponseType(typeof(List<VendorSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Vendors(
        [FromQuery] string? search = null, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        return Ok(await _sql.GetVendorsAsync(term, Math.Clamp(limit, 1, 200), ct));
    }

    public sealed class CreatePurchaseOrderBody
    {
        public string? CardCode { get; set; }
    }
}
