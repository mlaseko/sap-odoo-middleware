using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Unified re-sync endpoint for updating existing SAP documents with the latest
/// Odoo payload.  Used when the initial creation succeeded but some fields
/// (UDFs, amounts, account mappings) were missing or incorrect.
///
/// The caller selects a <c>document_type</c> and provides the SAP <c>doc_entry</c>.
/// The endpoint routes to the appropriate update service method, re-applies
/// all fields from the request body, and optionally writes back to Odoo.
/// </summary>
[ApiController]
[Route("api/resync")]
public class ResyncController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly ILogger<ResyncController> _logger;

    public ResyncController(ISapB1Service sapService, ILogger<ResyncController> logger)
    {
        _sapService = sapService;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/resync
    /// Re-syncs a SAP document by updating it with a fresh payload from Odoo.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Resync([FromBody] ResyncRequest request)
    {
        _logger.LogInformation(
            "Re-sync request — DocumentType={DocumentType}, DocEntry={DocEntry}",
            request.DocumentType, request.DocEntry);

        try
        {
            object result = request.DocumentType.ToLowerInvariant() switch
            {
                "sales_order" => await _sapService.UpdateSalesOrderAsync(
                    request.DocEntry, request.SalesOrder
                        ?? throw new ArgumentException("sales_order payload is required")),

                "invoice" => await _sapService.UpdateInvoiceAsync(
                    request.DocEntry, request.Invoice
                        ?? throw new ArgumentException("invoice payload is required")),

                "incoming_payment" => await _sapService.UpdateIncomingPaymentAsync(
                    request.DocEntry, request.IncomingPayment
                        ?? throw new ArgumentException("incoming_payment payload is required")),

                _ => throw new ArgumentException(
                    $"Unsupported document type: '{request.DocumentType}'. " +
                    "Supported types: sales_order, invoice, incoming_payment")
            };

            _logger.LogInformation(
                "✅ Re-sync completed — DocumentType={DocumentType}, DocEntry={DocEntry}",
                request.DocumentType, request.DocEntry);

            return Ok(ApiResponse<object>.Ok(result));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Re-sync validation error");
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Re-sync failed — DocumentType={DocumentType}, DocEntry={DocEntry}",
                request.DocumentType, request.DocEntry);
            return StatusCode(500, ApiResponse<object>.Fail(ex.Message));
        }
    }
}
