using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Receives Incoming Payment requests from Odoo and creates them in SAP B1 via DI API.
/// After successful creation, writes SAP DocEntry and DocNum back to the Odoo payment
/// when <c>odoo_payment_id</c> is provided in the request.
/// </summary>
[ApiController]
[Route("api/incoming-payments")]
public class IncomingPaymentsController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly IOdooService _odooService;
    private readonly ILogger<IncomingPaymentsController> _logger;

    public IncomingPaymentsController(
        ISapB1Service sapService,
        IOdooService odooService,
        ILogger<IncomingPaymentsController> logger)
    {
        _sapService = sapService;
        _odooService = odooService;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/incoming-payments
    /// Creates an Incoming Payment (ORCT) in SAP B1 and, when <c>odoo_payment_id</c> is provided,
    /// writes the SAP DocEntry and DocNum back to the Odoo payment record.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SapIncomingPaymentRequest request)
    {
        _logger.LogInformation(
            "Received Incoming Payment creation request — ExternalPaymentId={ExternalPaymentId}, " +
            "CustomerCode={CustomerCode}, DocDate={DocDate}, Currency={Currency}, " +
            "PaymentTotal={PaymentTotal}, IsPartial={IsPartial}, JournalCode={JournalCode}, " +
            "BankOrCashAccountCode={BankOrCashAccountCode}, IsCashPayment={IsCashPayment}, " +
            "OdooPaymentId={OdooPaymentId}, LineCount={LineCount}",
            request.ExternalPaymentId,
            request.CustomerCode,
            request.DocDate,
            request.Currency,
            request.PaymentTotal,
            request.IsPartial,
            request.JournalCode,
            request.BankOrCashAccountCode,
            request.IsCashPayment,
            request.OdooPaymentId,
            request.Lines.Count);

        try
        {
            // Step 1: Create the Incoming Payment in SAP B1
            var result = await _sapService.CreateIncomingPaymentAsync(request);

            _logger.LogInformation(
                "SAP Incoming Payment created: DocEntry={DocEntry}, DocNum={DocNum}, " +
                "ExternalPaymentId={ExternalPaymentId}, OdooPaymentId={OdooPaymentId}, " +
                "TotalApplied={TotalApplied}, LineCount={LineCount}",
                result.DocEntry,
                result.DocNum,
                result.ExternalPaymentId,
                result.OdooPaymentId,
                result.TotalApplied,
                request.Lines.Count);

            // Step 2: Write back SAP fields to Odoo (when OdooPaymentId is provided)
            if (request.OdooPaymentId.HasValue && request.OdooPaymentId.Value > 0)
            {
                await WriteBackToOdoo(request.OdooPaymentId.Value, result);
            }
            else
            {
                _logger.LogInformation(
                    "Skipping Odoo write-back — OdooPaymentId not provided in request.");
            }

            return Ok(ApiResponse<SapIncomingPaymentResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create SAP Incoming Payment for ExternalPaymentId={ExternalPaymentId}, " +
                "CustomerCode={CustomerCode}",
                request.ExternalPaymentId,
                request.CustomerCode);

            return StatusCode(500, ApiResponse<SapIncomingPaymentResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Writes SAP Incoming Payment DocEntry and DocNum back to Odoo.
    /// Failures are logged and attached to the response but do NOT fail the overall request
    /// (the SAP Incoming Payment was already created successfully).
    /// </summary>
    private async Task WriteBackToOdoo(int odooPaymentId, SapIncomingPaymentResponse result)
    {
        try
        {
            _logger.LogInformation(
                "Starting Odoo write-back — OdooPaymentId={OdooPaymentId}, SapDocEntry={SapDocEntry}, SapDocNum={SapDocNum}",
                odooPaymentId, result.DocEntry, result.DocNum);

            var writeBackRequest = new IncomingPaymentWriteBackRequest
            {
                OdooPaymentId = odooPaymentId,
                SapDocEntry = result.DocEntry,
                SapDocNum = result.DocNum
            };

            await _odooService.UpdateIncomingPaymentAsync(writeBackRequest);

            result.OdooWriteBackSuccess = true;

            _logger.LogInformation(
                "Odoo write-back completed — OdooPaymentId={OdooPaymentId}, SapDocEntry={SapDocEntry}",
                odooPaymentId, result.DocEntry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Odoo write-back failed for OdooPaymentId={OdooPaymentId}, SapDocEntry={SapDocEntry}. " +
                "SAP Incoming Payment was created successfully — manual reconciliation may be needed.",
                odooPaymentId, result.DocEntry);

            result.OdooWriteBackSuccess = false;
            result.OdooWriteBackError = ex.Message;
        }
    }
}
