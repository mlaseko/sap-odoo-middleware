using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.PurchaseSync;
using SapOdooMiddleware.Services;
using SapOdooMiddleware.Services.PurchaseSync;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Odoo → SAP supplier A/P invoice (phase A of the invoice-to-stock plan, 5 Nov 2026): called by the Integration
/// Control Centre when "Create SAP A/P invoice" is pressed on a container order. Creates a SAP A/P invoice draft from
/// the supplier invoice Odoo holds, on the container's GRPO; the accountant reviews the draft in SAP and adds it.
/// </summary>
[ApiController]
[Route("api/ap-invoices")]
public class ApInvoicesController : ControllerBase
{
    private readonly ISapB1Service _sap;
    private readonly ILogger<ApInvoicesController> _logger;

    public ApInvoicesController(ISapB1Service sap, ILogger<ApInvoicesController> logger)
    {
        _sap = sap;
        _logger = logger;
    }

    /// <summary>POST /api/ap-invoices/draft — SAP A/P invoice draft for a container order.</summary>
    [HttpPost("draft")]
    [ProducesResponseType(typeof(ApiResponse<ApInvoiceDraftResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<ApInvoiceDraftResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<ApInvoiceDraftResult>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateDraft([FromBody] ApInvoiceDraftRequest request)
    {
        var errors = ApInvoiceDraftPlanning.Validate(request);
        if (errors.Count > 0)
            return BadRequest(ApiResponse<ApInvoiceDraftResult>.Fail(errors));

        _logger.LogInformation(
            "A/P invoice draft requested: {Order}, {CardCode}/{Invoice}, GRPO {Grpo}, {Lines} lines, freight {Freight}, total {Total} {Currency}",
            request.OdooShipmentId, request.CardCode, request.InvoiceNumber, request.GrpoDocentry, request.Lines.Count,
            request.FreightFc, request.InvoiceTotalFc, request.Currency);

        try
        {
            var result = await _sap.CreateApInvoiceDraftAsync(request);
            return Ok(ApiResponse<ApInvoiceDraftResult>.Ok(result));
        }
        catch (ApInvoiceGuardException ex)
        {
            _logger.LogWarning("A/P invoice draft refused for {Order}: {Message}", request.OdooShipmentId, ex.Message);
            return UnprocessableEntity(ApiResponse<ApInvoiceDraftResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A/P invoice draft failed for {Order}", request.OdooShipmentId);
            return StatusCode(500, ApiResponse<ApInvoiceDraftResult>.Fail(ex.Message));
        }
    }
}
