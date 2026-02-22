using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Receives Sales Orders from Odoo and creates them in SAP B1 via DI API.
/// </summary>
[ApiController]
[Route("api/sales-orders")]
public class SalesOrdersController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly ILogger<SalesOrdersController> _logger;

    public SalesOrdersController(ISapB1Service sapService, ILogger<SalesOrdersController> logger)
    {
        _sapService = sapService;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/sales-orders
    /// Creates a Sales Order in SAP B1 and optionally a Pick List.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SapSalesOrderRequest request)
    {
        _logger.LogInformation("Received SO creation request for Odoo ref {UOdooSoId}", request.ResolvedSoId);

        try
        {
            var result = await _sapService.CreateSalesOrderAsync(request);

            _logger.LogInformation(
                "SAP SO created: DocEntry={DocEntry}, DocNum={DocNum}, PickList={PickList}",
                result.DocEntry, result.DocNum, result.PickListEntry);

            return Ok(ApiResponse<SapSalesOrderResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create SAP Sales Order for Odoo ref {UOdooSoId}", request.ResolvedSoId);
            return StatusCode(500, ApiResponse<SapSalesOrderResponse>.Fail(ex.Message));
        }
    }
}
