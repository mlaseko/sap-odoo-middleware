using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Receives delivery confirmations from SAP B1 and updates Odoo stock.picking.
/// </summary>
[ApiController]
[Route("api/deliveries")]
public class DeliveriesController : ControllerBase
{
    private readonly IOdooService _odooService;
    private readonly ILogger<DeliveriesController> _logger;

    public DeliveriesController(IOdooService odooService, ILogger<DeliveriesController> logger)
    {
        _odooService = odooService;
        _logger = logger;
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
