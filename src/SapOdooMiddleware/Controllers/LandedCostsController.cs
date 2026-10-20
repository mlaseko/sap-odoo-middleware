using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.PurchaseSync;
using SapOdooMiddleware.Services;
using SapOdooMiddleware.Services.PurchaseSync;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Odoo → SAP landed costs (spec v4 §6.3, Flow E), called by the Integration Control Centre's landed-cost job when a
/// container order reaches "Costs final": one SAP Landed Costs document per push, on the container's GRPO (first push)
/// or on the previous landed cost document (delta), idempotent on <c>OIPF.U_OdooShipmentId</c>.
/// </summary>
[ApiController]
[Route("api/landed-costs")]
public class LandedCostsController : ControllerBase
{
    private readonly ISapB1Service _sap;
    private readonly ILogger<LandedCostsController> _logger;

    public LandedCostsController(ISapB1Service sap, ILogger<LandedCostsController> logger)
    {
        _sap = sap;
        _logger = logger;
    }

    /// <summary>POST /api/landed-costs — SAP Landed Costs document for a container order.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<LandedCostPushResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<LandedCostPushResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<LandedCostPushResult>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] LandedCostPushRequest request)
    {
        if (request.PushSequence <= 0) request.PushSequence = 1;

        var errors = LandedCostPlanning.Validate(request);
        if (errors.Count > 0)
            return BadRequest(ApiResponse<LandedCostPushResult>.Fail(errors));

        string key = LandedCostPlanning.IdempotencyKey(request.OdooShipmentId, request.PushSequence);
        _logger.LogInformation(
            "Landed cost push requested: key={Key}, GRPO={Grpo}, previous LC={Previous}, lines={Lines}, approve_negative={Approve}",
            key, request.GrpoDocentry, request.PreviousLcDocentry, request.CostLines.Count, request.ApproveNegative);

        try
        {
            var result = await _sap.CreateLandedCostAsync(request);
            return Ok(ApiResponse<LandedCostPushResult>.Ok(result));
        }
        catch (LandedCostGuardException ex)
        {
            // A §6.3 guard: nothing was created; the job fails with the code and is not retried automatically.
            _logger.LogWarning("Landed cost push refused: key={Key}: {Message}", key, ex.Message);
            return UnprocessableEntity(ApiResponse<LandedCostPushResult>.Fail(ex.Message));
        }
        catch (SapConflictException ex)
        {
            _logger.LogWarning("Landed cost push conflict: key={Key}: {Message}", key, ex.Message);
            return Conflict(ApiResponse<LandedCostPushResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Landed cost push failed: key={Key}", key);
            return StatusCode(500, ApiResponse<LandedCostPushResult>.Fail(ex.Message));
        }
    }
}
