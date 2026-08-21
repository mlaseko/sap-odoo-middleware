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
    /// POST /api/payments/{docEntry}/cancel
    /// Cancels an Incoming Payment (ORCT) via DI API <c>Payments.GetByKey</c> +
    /// <c>Cancel()</c>. Pre-checks ORCT.Canceled: an already-cancelled payment returns
    /// success idempotently with <c>already_cancelled = true</c>. SAP's rejection
    /// messages (deposited or reconciled payments) are passed through verbatim.
    /// The cancelled DocNum is logged.
    /// </summary>
    [HttpPost("/api/payments/{docEntry:int}/cancel")]
    [ProducesResponseType(typeof(ApiResponse<SapPaymentCancelResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<SapPaymentCancelResponse>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<SapPaymentCancelResponse>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Cancel(int docEntry)
    {
        _logger.LogInformation("Incoming Payment cancellation requested: DocEntry={DocEntry}", docEntry);

        try
        {
            var result = await _sapService.CancelIncomingPaymentAsync(docEntry);
            return Ok(ApiResponse<SapPaymentCancelResponse>.Ok(result));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(ApiResponse<SapPaymentCancelResponse>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            // SAP's own message (e.g. deposited/reconciled rejection) flows through as-is.
            _logger.LogError(ex, "Incoming Payment cancellation failed: DocEntry={DocEntry}", docEntry);
            return StatusCode(500, ApiResponse<SapPaymentCancelResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// POST /api/payments/cancel-by-invoice/{invoiceDocEntry}
    /// Cancels the Incoming Payment that was applied to the given AR Invoice (OINV
    /// DocEntry). The middleware resolves the payment via the RCT2 allocation lines
    /// and cancels THAT payment — no new payment is posted by us; SAP's own
    /// cancellation document carries the reversal postings.
    /// <list type="bullet">
    ///   <item>No payment found for the invoice → 404.</item>
    ///   <item>Payment already cancelled → 200 with <c>already_cancelled = true</c> (idempotent).</item>
    ///   <item>Multiple active payments on the invoice → 409 listing them; cancel the
    ///   intended one explicitly via <c>POST /api/payments/{docEntry}/cancel</c>.</item>
    /// </list>
    /// </summary>
    [HttpPost("/api/payments/cancel-by-invoice/{invoiceDocEntry:int}")]
    [ProducesResponseType(typeof(ApiResponse<SapPaymentCancelResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<SapPaymentCancelResponse>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<SapPaymentCancelResponse>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<SapPaymentCancelResponse>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CancelByInvoice(int invoiceDocEntry)
    {
        _logger.LogInformation(
            "Payment cancellation by invoice requested: InvoiceDocEntry={InvoiceDocEntry}", invoiceDocEntry);

        try
        {
            var payments = await _sapService.FindIncomingPaymentsByInvoiceAsync(invoiceDocEntry);
            if (payments.Count == 0)
                return NotFound(ApiResponse<SapPaymentCancelResponse>.Fail(
                    $"No incoming payment found for invoice DocEntry={invoiceDocEntry} (RCT2)."));

            var active = payments.Where(p => !p.Cancelled).ToList();

            // Everything already cancelled → idempotent success.
            if (active.Count == 0)
            {
                var latest = payments[0];
                return Ok(ApiResponse<SapPaymentCancelResponse>.Ok(
                    new SapPaymentCancelResponse
                    {
                        DocEntry = latest.DocEntry,
                        DocNum = latest.DocNum,
                        AlreadyCancelled = true,
                    },
                    new Dictionary<string, object> { ["invoice_doc_entry"] = invoiceDocEntry }));
            }

            // Ambiguous — never guess which payment to reverse.
            if (active.Count > 1)
            {
                var listing = string.Join(", ", active.Select(p => $"DocEntry={p.DocEntry} (DocNum {p.DocNum})"));
                return Conflict(ApiResponse<SapPaymentCancelResponse>.Fail(
                    $"Invoice DocEntry={invoiceDocEntry} has {active.Count} active incoming payments: {listing}. " +
                    "Cancel the intended one explicitly via POST /api/payments/{docEntry}/cancel."));
            }

            var result = await _sapService.CancelIncomingPaymentAsync(active[0].DocEntry);
            return Ok(ApiResponse<SapPaymentCancelResponse>.Ok(
                result,
                new Dictionary<string, object> { ["invoice_doc_entry"] = invoiceDocEntry }));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(ApiResponse<SapPaymentCancelResponse>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            // SAP's own rejection (e.g. deposited/reconciled payment) flows through as-is.
            _logger.LogError(ex,
                "Payment cancellation by invoice failed: InvoiceDocEntry={InvoiceDocEntry}", invoiceDocEntry);
            return StatusCode(500, ApiResponse<SapPaymentCancelResponse>.Fail(ex.Message));
        }
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
    /// PUT /api/incoming-payments/{docEntry}
    /// Updates UDF fields on an existing Incoming Payment in SAP B1 (re-sync).
    /// </summary>
    [HttpPut("{docEntry:int}")]
    public async Task<IActionResult> Update(int docEntry, [FromBody] SapIncomingPaymentRequest request)
    {
        _logger.LogInformation(
            "Received Incoming Payment update request — DocEntry={DocEntry}, ExternalPaymentId={ExternalPaymentId}",
            docEntry, request.ExternalPaymentId);

        try
        {
            var result = await _sapService.UpdateIncomingPaymentAsync(docEntry, request);

            _logger.LogInformation(
                "SAP Incoming Payment updated: DocEntry={DocEntry}, DocNum={DocNum}",
                result.DocEntry, result.DocNum);

            // Write back to Odoo if OdooPaymentId is provided
            if (request.OdooPaymentId.HasValue && request.OdooPaymentId.Value > 0)
            {
                await WriteBackToOdoo(request.OdooPaymentId.Value, result);
            }

            return Ok(ApiResponse<SapIncomingPaymentResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update SAP Incoming Payment DocEntry={DocEntry}", docEntry);
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
