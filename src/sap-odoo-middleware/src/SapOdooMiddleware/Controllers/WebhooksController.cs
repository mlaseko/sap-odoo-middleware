using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Services.Odoo;

namespace SapOdooMiddleware.Controllers;

[ApiController]
[Route("api/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly IOdooJsonRpcClient _odooClient;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(IOdooJsonRpcClient odooClient, ILogger<WebhooksController> logger)
    {
        _odooClient = odooClient;
        _logger = logger;
    }

    /// <summary>
    /// Receive delivery confirmation from SAP and update Odoo.
    /// SAP sends header-only: {odoo_so_ref, sap_delivery_no, delivery_date, status}.
    /// Middleware finds the sale.order in Odoo, reserves stock, sets quantities, and validates the picking.
    /// </summary>
    [HttpPost("delivery-confirmation")]
    [ProducesResponseType(typeof(ApiResponse<DeliveryConfirmationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> DeliveryConfirmation([FromBody] DeliveryConfirmationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OdooSoRef))
        {
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "odoo_so_ref is required"));
        }

        if (request.SapDeliveryNo <= 0)
        {
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "sap_delivery_no must be a positive number"));
        }

        if (string.IsNullOrWhiteSpace(request.DeliveryDate))
        {
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "delivery_date is required"));
        }

        try
        {
            _logger.LogInformation(
                "Processing delivery confirmation: OdooSoRef={OdooSoRef}, SapDeliveryNo={SapDeliveryNo}",
                request.OdooSoRef, request.SapDeliveryNo);

            var result = await _odooClient.ConfirmDeliveryAsync(request);
            return Ok(ApiResponse<DeliveryConfirmationResponse>.Ok(result));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Error confirming delivery for {OdooSoRef}", request.OdooSoRef);
            return StatusCode(StatusCodes.Status502BadGateway,
                ApiResponse<object>.Fail("ODOO_CONNECTION_FAILED", "Failed to process delivery in Odoo", ex.Message));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error confirming delivery for {OdooSoRef}", request.OdooSoRef);
            return StatusCode(StatusCodes.Status502BadGateway,
                ApiResponse<object>.Fail("ODOO_CONNECTION_FAILED", "Failed to connect to Odoo", ex.Message));
        }
    }
}
