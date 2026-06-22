using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Receives Goods Return requests from Odoo and creates them in SAP B1 via DI API.
/// After successful creation, writes SAP DocEntry back to the Odoo return picking
/// when <c>odoo_picking_id</c> is provided in the request.
/// </summary>
[ApiController]
[Route("api/returns")]
public class ReturnsController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly IOdooService _odooService;
    private readonly ILogger<ReturnsController> _logger;

    public ReturnsController(
        ISapB1Service sapService,
        IOdooService odooService,
        ILogger<ReturnsController> logger)
    {
        _sapService = sapService;
        _odooService = odooService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/returns/{docEntry}/status
    /// Returns the document status (open/closed) of a Return Request (ORRR) in SAP B1.
    /// Odoo gates return validation on this — the return picking can only be validated
    /// once the Return Request is closed in SAP (inventory adjusted).
    /// </summary>
    [HttpGet("{docEntry:int}/status")]
    public async Task<IActionResult> GetStatus(int docEntry)
    {
        _logger.LogInformation(
            "Received Return Request status request — DocEntry={DocEntry}", docEntry);

        try
        {
            var result = await _sapService.GetReturnRequestStatusAsync(docEntry);

            _logger.LogInformation(
                "Return Request status: DocEntry={DocEntry}, DocNum={DocNum}, Status={Status}",
                result.DocEntry, result.DocNum, result.Status);

            return Ok(ApiResponse<SapReturnRequestStatusResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to get Return Request status for DocEntry={DocEntry}", docEntry);

            return StatusCode(500, ApiResponse<SapReturnRequestStatusResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// POST /api/returns
    /// Creates a Return Request (ORRR) in SAP B1 by Copy-To from the
    /// A/R Invoice (OINV).  The invoice must be open.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SapGoodsReturnRequest request)
    {
        _logger.LogInformation(
            "Received Goods Return creation request — ExternalReturnId={ExternalReturnId}, " +
            "CustomerCode={CustomerCode}, OdooPickingId={OdooPickingId}, LineCount={LineCount}",
            request.ExternalReturnId,
            request.CustomerCode,
            request.OdooPickingId,
            request.Lines.Count);

        try
        {
            var result = await _sapService.CreateGoodsReturnAsync(request);

            _logger.LogInformation(
                "SAP Goods Return created: DocEntry={DocEntry}, DocNum={DocNum}, " +
                "ExternalReturnId={ExternalReturnId}",
                result.DocEntry, result.DocNum, result.ExternalReturnId);

            // Write back SAP fields to Odoo
            if (request.OdooPickingId.HasValue && request.OdooPickingId.Value > 0)
            {
                await WriteBackToOdoo(request.OdooPickingId.Value, result);
            }

            return Ok(ApiResponse<SapGoodsReturnResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create SAP Goods Return for ExternalReturnId={ExternalReturnId}",
                request.ExternalReturnId);

            return StatusCode(500, ApiResponse<SapGoodsReturnResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// PUT /api/returns/{docEntry}
    /// Updates UDF fields on an existing Goods Return in SAP B1 (re-sync).
    /// </summary>
    [HttpPut("{docEntry:int}")]
    public async Task<IActionResult> Update(int docEntry, [FromBody] SapGoodsReturnRequest request)
    {
        _logger.LogInformation(
            "Received Goods Return update request — DocEntry={DocEntry}, ExternalReturnId={ExternalReturnId}",
            docEntry, request.ExternalReturnId);

        try
        {
            var result = await _sapService.UpdateGoodsReturnAsync(docEntry, request);

            if (request.OdooPickingId.HasValue && request.OdooPickingId.Value > 0)
            {
                await WriteBackToOdoo(request.OdooPickingId.Value, result);
            }

            return Ok(ApiResponse<SapGoodsReturnResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update SAP Goods Return DocEntry={DocEntry}", docEntry);
            return StatusCode(500, ApiResponse<SapGoodsReturnResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// DELETE /api/returns/{docEntry}
    /// Cancels a Goods Return in SAP B1 when the return picking
    /// is cancelled in Odoo.
    /// </summary>
    [HttpDelete("{docEntry:int}")]
    public async Task<IActionResult> Cancel(int docEntry)
    {
        _logger.LogInformation(
            "Received Goods Return cancel request — DocEntry={DocEntry}",
            docEntry);

        try
        {
            await _sapService.CancelGoodsReturnAsync(docEntry);

            _logger.LogInformation(
                "SAP Goods Return cancelled: DocEntry={DocEntry}", docEntry);

            return Ok(ApiResponse<object>.Ok(new { doc_entry = docEntry, action = "cancelled" }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to cancel SAP Goods Return DocEntry={DocEntry}", docEntry);

            return StatusCode(500, ApiResponse<object>.Fail(ex.Message));
        }
    }

    private async Task WriteBackToOdoo(int odooPickingId, SapGoodsReturnResponse result)
    {
        try
        {
            _logger.LogInformation(
                "Starting Odoo write-back — OdooPickingId={OdooPickingId}, SapDocEntry={SapDocEntry}",
                odooPickingId, result.DocEntry);

            await _odooService.UpdateGoodsReturnAsync(new GoodsReturnWriteBackRequest
            {
                OdooPickingId = odooPickingId,
                SapDocEntry = result.DocEntry
            });

            result.OdooWriteBackSuccess = true;

            _logger.LogInformation(
                "Odoo write-back completed — OdooPickingId={OdooPickingId}, SapDocEntry={SapDocEntry}",
                odooPickingId, result.DocEntry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Odoo write-back failed for OdooPickingId={OdooPickingId}. " +
                "SAP Goods Return was created successfully — manual update may be needed.",
                odooPickingId);

            result.OdooWriteBackSuccess = false;
            result.OdooWriteBackError = ex.Message;
        }
    }
}
