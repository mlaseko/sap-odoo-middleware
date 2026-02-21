using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Endpoints for SAP B1 connectivity diagnostics.
/// </summary>
[ApiController]
[Route("api/sapb1")]
public class SapB1Controller : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly ILogger<SapB1Controller> _logger;

    public SapB1Controller(ISapB1Service sapService, ILogger<SapB1Controller> logger)
    {
        _sapService = sapService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/sapb1/ping
    /// Verifies SAP B1 DI API connectivity and returns non-secret connection details.
    /// </summary>
    [HttpGet("ping")]
    public async Task<IActionResult> Ping()
    {
        _logger.LogInformation("SAP B1 ping requested.");

        try
        {
            var result = await _sapService.PingAsync();
            return Ok(ApiResponse<SapB1PingResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SAP B1 ping failed.");
            return StatusCode(500, ApiResponse<SapB1PingResponse>.Fail(ex.Message));
        }
    }
}
