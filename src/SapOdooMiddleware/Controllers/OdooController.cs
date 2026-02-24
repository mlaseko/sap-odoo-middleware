using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Endpoints for Odoo connectivity diagnostics.
/// </summary>
[ApiController]
[Route("api/odoo")]
public class OdooController : ControllerBase
{
    private readonly IOdooService _odooService;
    private readonly ILogger<OdooController> _logger;

    public OdooController(IOdooService odooService, ILogger<OdooController> logger)
    {
        _odooService = odooService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/odoo/ping
    /// Verifies Odoo JSON-RPC connectivity by authenticating and returning session info.
    /// Does not modify any data in Odoo.
    /// </summary>
    [HttpGet("ping")]
    public async Task<IActionResult> Ping()
    {
        _logger.LogInformation("Odoo ping requested.");

        try
        {
            var result = await _odooService.PingAsync();
            return Ok(ApiResponse<OdooPingResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Odoo ping failed.");
            return StatusCode(500, ApiResponse<OdooPingResponse>.Fail(ex.Message));
        }
    }
}
