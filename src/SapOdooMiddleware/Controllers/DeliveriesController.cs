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
