using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Receives delivery confirmations from SAP B1 and updates Odoo stock.picking.
/// Also provides delivery status lookups for goods-return validation.
/// </summary>
[ApiController]
[Route("api/deliveries")]
public class DeliveriesController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly IOdooService _odooService;
    private readonly ILogger<DeliveriesController> _logger;

    public DeliveriesController(
        ISapB1Service sapService,
        IOdooService odooService,
        ILogger<DeliveriesController> logger)
    {
        _sapService = sapService;
        _odooService = odooService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/deliveries/{docEntry}/status
    /// Returns the document status (open/closed) of a Delivery Note (ODLN) in SAP B1.
    /// Used by Odoo to validate that a goods return can be created against this delivery.
    /// </summary>
    [HttpGet("{docEntry:int}/status")]
    public async Task<IActionResult> GetStatus(int docEntry)
    {
        _logger.LogInformation(
            "Received Delivery Note status request — DocEntry={DocEntry}", docEntry);

        try
        {
            var result = await _sapService.GetDeliveryStatusAsync(docEntry);

            _logger.LogInformation(
                "Delivery Note status: DocEntry={DocEntry}, DocNum={DocNum}, Status={Status}",
                result.DocEntry, result.DocNum, result.Status);

            return Ok(ApiResponse<SapDeliveryStatusResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to get Delivery Note status for DocEntry={DocEntry}", docEntry);

            return StatusCode(500, ApiResponse<SapDeliveryStatusResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// GET /api/deliveries/by-order/{soDocEntry}
    /// Finds the Delivery Note (ODLN) created from a given Sales Order DocEntry.
    /// Used by the SAP Field Sync wizard when the delivery is missing its SAP
    /// DocEntry but the related SO already has one.
    ///
    /// Traces: DLN1.BaseEntry = soDocEntry WHERE BaseType = 17 (Sales Order).
    /// Returns the delivery's DocEntry, DocNum, and status.
    /// </summary>
    [HttpGet("by-order/{soDocEntry:int}")]
    public async Task<IActionResult> FindByOrder(int soDocEntry)
    {
        _logger.LogInformation(
            "Find Delivery by SO DocEntry — soDocEntry={SoDocEntry}", soDocEntry);

        try
        {
            var result = await _sapService.FindDeliveryByOrderAsync(soDocEntry);

            if (result == null)
            {
                return NotFound(ApiResponse<SapDeliveryStatusResponse>.Fail(
                    $"No delivery note found for Sales Order DocEntry={soDocEntry}."));
            }

            _logger.LogInformation(
                "Delivery found for SO {SoDocEntry}: DocEntry={DocEntry}, DocNum={DocNum}, Status={Status}",
                soDocEntry, result.DocEntry, result.DocNum, result.Status);

            return Ok(ApiResponse<SapDeliveryStatusResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to find delivery for SO DocEntry={SoDocEntry}", soDocEntry);

            return StatusCode(500, ApiResponse<SapDeliveryStatusResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// POST /api/deliveries
    /// Confirms a delivery in Odoo after SAP Delivery Note is posted.
    /// Header-only payload: {odoo_so_ref, sap_delivery_no, delivery_date, status}.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DeliveryUpdateRequest request)
    {
        _logger.LogInformation(
            "Received delivery update: UOdooSoId={UOdooSoId}, SapDeliveryNo={SapDeliveryNo}",
            request.ResolvedSoId, request.SapDeliveryNo);

        try
        {
            var result = await _odooService.ConfirmDeliveryAsync(request);

            _logger.LogInformation(
                "Odoo delivery confirmed: PickingId={PickingId}, Name={Name}, State={State}",
                result.PickingId, result.PickingName, result.State);

            return Ok(ApiResponse<DeliveryUpdateResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to confirm delivery in Odoo for ref {UOdooSoId}", request.ResolvedSoId);
            return StatusCode(500, ApiResponse<DeliveryUpdateResponse>.Fail(ex.Message));
        }
    }
}
