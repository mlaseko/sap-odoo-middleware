using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Services.Sap;

namespace SapOdooMiddleware.Controllers;

[ApiController]
[Route("api/orders")]
public class OrdersController : ControllerBase
{
    private readonly ISapServiceLayerClient _sapClient;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(ISapServiceLayerClient sapClient, ILogger<OrdersController> logger)
    {
        _sapClient = sapClient;
        _logger = logger;
    }

    /// <summary>
    /// Create a sales order in SAP B1 from Odoo.
    /// </summary>
    [HttpPost("sales-order")]
    [ProducesResponseType(typeof(ApiResponse<SalesOrderResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> CreateSalesOrder([FromBody] SalesOrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerCardCode))
        {
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "customer_card_code is required"));
        }

        if (string.IsNullOrWhiteSpace(request.OdooOrderRef))
        {
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "odoo_order_ref is required"));
        }

        if (request.Lines == null || request.Lines.Count == 0)
        {
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "At least one order line is required"));
        }

        foreach (var line in request.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.ItemCode))
            {
                return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "item_code is required on all lines"));
            }

            if (line.Quantity <= 0)
            {
                return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR",
                    $"quantity must be greater than 0 for item {line.ItemCode}"));
            }
        }

        try
        {
            _logger.LogInformation("Creating sales order for Odoo ref: {OdooRef}", request.OdooOrderRef);
            var result = await _sapClient.CreateSalesOrderAsync(request);
            return StatusCode(StatusCodes.Status201Created, ApiResponse<SalesOrderResponse>.Ok(result));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "SAP Service Layer error creating sales order for {OdooRef}", request.OdooOrderRef);
            return StatusCode(StatusCodes.Status502BadGateway,
                ApiResponse<object>.Fail("SAP_SL_REQUEST_FAILED", "Failed to create sales order in SAP", ex.Message));
        }
    }
}
