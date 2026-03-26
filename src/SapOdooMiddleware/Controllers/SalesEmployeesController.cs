using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Receives Sales Employee requests from Odoo and creates/updates/lists
/// them in SAP B1 OSLP table via DI API.
///
/// POST /api/sales-employees            — create a new sales employee
/// PUT  /api/sales-employees/{slpCode}  — update an existing sales employee
/// GET  /api/sales-employees            — list all sales employees (for sync)
/// </summary>
[ApiController]
[Route("api/sales-employees")]
public class SalesEmployeesController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly ILogger<SalesEmployeesController> _logger;

    public SalesEmployeesController(ISapB1Service sapService, ILogger<SalesEmployeesController> logger)
    {
        _sapService = sapService;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/sales-employees
    /// Creates a Sales Employee in SAP B1 OSLP table.
    /// Returns the auto-generated SlpCode so Odoo can write it back.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SapSalesEmployeeRequest request)
    {
        _logger.LogInformation(
            "Received sales employee creation request — OdooEmployeeId={OdooEmployeeId}, SlpName={SlpName}",
            request.OdooEmployeeId, request.SlpName);

        try
        {
            var result = await _sapService.CreateSalesEmployeeAsync(request);

            _logger.LogInformation(
                "SAP Sales Employee created: SlpCode={SlpCode}, SlpName={SlpName}, OdooId={OdooId}",
                result.SlpCode, result.SlpName, result.OdooEmployeeId);

            return Ok(ApiResponse<SapSalesEmployeeResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create SAP Sales Employee for OdooEmployeeId={OdooEmployeeId}",
                request.OdooEmployeeId);
            return StatusCode(500, ApiResponse<SapSalesEmployeeResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// PUT /api/sales-employees/{slpCode}
    /// Updates an existing Sales Employee in SAP B1.
    /// </summary>
    [HttpPut("{slpCode:int}")]
    public async Task<IActionResult> Update(int slpCode, [FromBody] SapSalesEmployeeRequest request)
    {
        _logger.LogInformation(
            "Received sales employee update request — SlpCode={SlpCode}, SlpName={SlpName}, OdooEmployeeId={OdooEmployeeId}",
            slpCode, request.SlpName, request.OdooEmployeeId);

        try
        {
            var result = await _sapService.UpdateSalesEmployeeAsync(slpCode, request);

            _logger.LogInformation(
                "SAP Sales Employee updated: SlpCode={SlpCode}, SlpName={SlpName}",
                result.SlpCode, result.SlpName);

            return Ok(ApiResponse<SapSalesEmployeeResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to update SAP Sales Employee SlpCode={SlpCode}", slpCode);
            return StatusCode(500, ApiResponse<SapSalesEmployeeResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// GET /api/sales-employees
    /// Lists all Sales Employees from SAP B1 OSLP table.
    /// Used for one-time sync to match existing records.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        _logger.LogInformation("Received request to list all SAP Sales Employees");

        try
        {
            var result = await _sapService.ListSalesEmployeesAsync();

            _logger.LogInformation("Retrieved {Count} Sales Employees from SAP", result.Count);

            return Ok(ApiResponse<List<SapSalesEmployeeResponse>>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list SAP Sales Employees");
            return StatusCode(500, ApiResponse<List<SapSalesEmployeeResponse>>.Fail(ex.Message));
        }
    }
}
