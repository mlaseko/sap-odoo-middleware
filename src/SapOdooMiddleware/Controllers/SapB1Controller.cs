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

    /// <summary>
    /// POST /api/sapb1/setup-udfs?dry_run=false&amp;force=false
    /// Creates all required User-Defined Fields (UDFs) in SAP B1 if they don't exist (safe to call
    /// repeatedly). Refuses (409) while other users are connected to the company database unless
    /// <c>force=true</c>; <c>dry_run=true</c> only lists the connected sessions.
    /// Same routine as POST /api/admin/setup-sap-udfs.
    /// </summary>
    [HttpPost("setup-udfs")]
    public async Task<IActionResult> SetupUdfs(
        [FromServices] SapUdfSetupService udfSetup,
        [FromQuery(Name = "dry_run")] bool dryRun = false,
        [FromQuery] bool force = false,
        CancellationToken ct = default)
    {
        _logger.LogInformation("UDF setup requested (dry_run={DryRun}, force={Force}).", dryRun, force);

        try
        {
            var result = await udfSetup.RunAsync(dryRun, force, ct);
            if (result.Refused)
                return StatusCode(409, ApiResponse<SapUdfSetupResult>.Ok(result));
            return Ok(ApiResponse<SapUdfSetupResult>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UDF setup failed.");
            return StatusCode(500, ApiResponse<SapUdfSetupResult>.Fail(ex.Message));
        }
    }
}
