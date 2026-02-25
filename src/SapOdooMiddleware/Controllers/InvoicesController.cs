using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Receives AR Invoice requests from Odoo and creates them in SAP B1 via DI API.
/// Invoices are preferably created by copying from the related Delivery Note (ODLN),
/// which maintains the Sales Order → Delivery → Invoice document chain.
/// </summary>
[ApiController]
[Route("api/invoices")]
public class InvoicesController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly ILogger<InvoicesController> _logger;

    public InvoicesController(ISapB1Service sapService, ILogger<InvoicesController> logger)
    {
        _sapService = sapService;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/invoices
    /// Creates an AR Invoice in SAP B1.
    ///
    /// Preferred flow: provide <c>sap_delivery_doc_entry</c> to create the invoice
    /// by copying from the Delivery Note, preserving the SO → Delivery → Invoice chain.
    ///
    /// Fallback: provide <c>lines</c> array for manual invoice creation.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SapInvoiceRequest request)
    {
        _logger.LogInformation(
            "Received AR Invoice creation request — ExternalInvoiceId={ExternalInvoiceId}, " +
            "CustomerCode={CustomerCode}, CopyFromDelivery={CopyFromDelivery}, " +
            "SapDeliveryDocEntry={SapDeliveryDocEntry}, SapSalesOrderDocEntry={SapSalesOrderDocEntry}, " +
            "LineCount={LineCount}",
            request.ExternalInvoiceId,
            request.CustomerCode,
            request.CopyFromDelivery,
            request.SapDeliveryDocEntry,
            request.SapSalesOrderDocEntry,
            request.Lines.Count);

        try
        {
            var result = await _sapService.CreateInvoiceAsync(request);

            _logger.LogInformation(
                "SAP AR Invoice created: DocEntry={DocEntry}, DocNum={DocNum}, " +
                "ExternalInvoiceId={ExternalInvoiceId}, BaseDeliveryDocEntry={BaseDeliveryDocEntry}, " +
                "BaseSalesOrderDocEntry={BaseSalesOrderDocEntry}",
                result.DocEntry,
                result.DocNum,
                result.ExternalInvoiceId,
                result.BaseDeliveryDocEntry,
                result.BaseSalesOrderDocEntry);

            return Ok(ApiResponse<SapInvoiceResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create SAP AR Invoice for ExternalInvoiceId={ExternalInvoiceId}, " +
                "SapDeliveryDocEntry={SapDeliveryDocEntry}",
                request.ExternalInvoiceId,
                request.SapDeliveryDocEntry);

            return StatusCode(500, ApiResponse<SapInvoiceResponse>.Fail(ex.Message));
        }
    }
}
