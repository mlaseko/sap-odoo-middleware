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
    /// POST /api/returns
    /// Creates a Goods Return (ORDN) in SAP B1, optionally by copying from the
    /// original Delivery Note (ODLN).
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
