using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Services.Odoo;
using SapOdooMiddleware.Services.Sap;

namespace SapOdooMiddleware.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    private readonly ISapServiceLayerClient _sapClient;
    private readonly IOdooJsonRpcClient _odooClient;
    private readonly ILogger<HealthController> _logger;

    public HealthController(
        ISapServiceLayerClient sapClient,
        IOdooJsonRpcClient odooClient,
        ILogger<HealthController> logger)
    {
        _sapClient = sapClient;
        _odooClient = odooClient;
        _logger = logger;
    }

    /// <summary>
    /// Detailed health check with SAP and Odoo connectivity status.
    /// </summary>
    [HttpGet("detailed")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DetailedHealthCheck()
    {
        var sapHealthy = await _sapClient.IsHealthyAsync();
        var odooHealthy = await _odooClient.IsHealthyAsync();
        var allHealthy = sapHealthy && odooHealthy;

        var healthData = new
        {
            status = allHealthy ? "healthy" : "degraded",
            timestamp = DateTime.UtcNow,
            services = new
            {
                sap_service_layer = new { status = sapHealthy ? "up" : "down" },
                odoo_jsonrpc = new { status = odooHealthy ? "up" : "down" }
            },
            version = "1.0.0"
        };

        return Ok(ApiResponse<object>.Ok(healthData));
    }
}
