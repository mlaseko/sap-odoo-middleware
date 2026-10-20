using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.PurchaseSync;
using SapOdooMiddleware.Services;
using SapOdooMiddleware.Services.PurchaseSync;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Odoo → SAP endpoints of the purchases integration (spec v4 §6.2), called by the Integration Control
/// Centre's purchases jobs: supplier payments and refunds ON ACCOUNT (never applied to an invoice, no SAP
/// internal reconciliation), their cancellation, and the bank-charges / small-balance adjustment journal
/// entries with their reversal. Every write is idempotent on a SAP UDF (<c>U_OdooPayRef</c>, <c>U_OdooRef</c>).
/// </summary>
[ApiController]
[Route("api")]
public class SupplierPushController : ControllerBase
{
    private readonly ISapB1Service _sap;
    private readonly ILogger<SupplierPushController> _logger;

    public SupplierPushController(ISapB1Service sap, ILogger<SupplierPushController> logger)
    {
        _sap = sap;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Flow B: outgoing payment on account (OVPM, object 46)
    // ------------------------------------------------------------------

    /// <summary>POST /api/outgoing-payments — SAP Outgoing Payment to the supplier, on account.</summary>
    [HttpPost("outgoing-payments")]
    [ProducesResponseType(typeof(ApiResponse<SupplierPaymentResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<SupplierPaymentResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<SupplierPaymentResult>), StatusCodes.Status409Conflict)]
    public Task<IActionResult> CreateOutgoingPayment([FromBody] SupplierPaymentRequest request) =>
        CreatePaymentAsync(request, isRefund: false);

    /// <summary>POST /api/outgoing-payments/{docEntry}/cancel — cancels the Outgoing Payment (idempotent).</summary>
    [HttpPost("outgoing-payments/{docEntry:int}/cancel")]
    public Task<IActionResult> CancelOutgoingPayment(int docEntry, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SupplierPaymentCancelRequest? request) =>
        CancelPaymentAsync(docEntry, request, isRefund: false);

    // ------------------------------------------------------------------
    // Flow C: supplier refund = incoming payment from the vendor, on account (ORCT, object 24)
    // ------------------------------------------------------------------

    /// <summary>POST /api/supplier-refunds — SAP Incoming Payment from the vendor, on account.</summary>
    [HttpPost("supplier-refunds")]
    [ProducesResponseType(typeof(ApiResponse<SupplierPaymentResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<SupplierPaymentResult>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<SupplierPaymentResult>), StatusCodes.Status409Conflict)]
    public Task<IActionResult> CreateSupplierRefund([FromBody] SupplierPaymentRequest request) =>
        CreatePaymentAsync(request, isRefund: true);

    /// <summary>POST /api/supplier-refunds/{docEntry}/cancel — cancels the refund's Incoming Payment (idempotent).</summary>
    [HttpPost("supplier-refunds/{docEntry:int}/cancel")]
    public Task<IActionResult> CancelSupplierRefund(int docEntry, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SupplierPaymentCancelRequest? request) =>
        CancelPaymentAsync(docEntry, request, isRefund: true);

    // ------------------------------------------------------------------
    // Flow D: adjustment journal entry (OJDT, object 30)
    // ------------------------------------------------------------------

    /// <summary>POST /api/supplier-adjustments — journal entry with a business-partner line and the mirror G/L line.</summary>
    [HttpPost("supplier-adjustments")]
    [ProducesResponseType(typeof(ApiResponse<SupplierAdjustmentResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<SupplierAdjustmentResult>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateSupplierAdjustment([FromBody] SupplierAdjustmentRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.OdooRef)) errors.Add("odoo_ref is required.");
        else if (request.OdooRef.Trim().Length > 50) errors.Add("odoo_ref must be at most 50 characters (U_OdooRef).");
        if (string.IsNullOrWhiteSpace(request.CardCode)) errors.Add("card_code is required.");
        if (string.IsNullOrWhiteSpace(request.SapGlCode)) errors.Add("sap_gl_code is required.");
        if (request.DocDate == default) errors.Add("doc_date is required.");
        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Trim().Length != 3) errors.Add("currency must be a 3-letter ISO code.");
        if (request.Amount <= 0) errors.Add("amount must be greater than zero.");
        if (request.Rate < 0) errors.Add("rate cannot be negative.");
        if (!AdjustmentPosting.IsValidDirection(request.Direction))
            errors.Add($"direction must be '{AdjustmentPosting.CreditSupplier}' or '{AdjustmentPosting.DebitSupplier}'.");
        if (errors.Count > 0)
            return BadRequest(ApiResponse<SupplierAdjustmentResult>.Fail(errors));

        _logger.LogInformation(
            "Supplier adjustment requested: ref={OdooRef}, card={CardCode}, {Direction} {Amount} {Currency} @ {Rate}, G/L {GlCode}, reason={Reason}",
            request.OdooRef, request.CardCode, request.Direction, request.Amount, request.Currency, request.Rate,
            request.SapGlCode, request.Reason);

        try
        {
            var result = await _sap.CreateSupplierAdjustmentJournalAsync(request);
            return Ok(ApiResponse<SupplierAdjustmentResult>.Ok(result));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<SupplierAdjustmentResult>.Fail(ex.Message));
        }
        catch (SapConflictException ex)
        {
            return Conflict(ApiResponse<SupplierAdjustmentResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Supplier adjustment failed: ref={OdooRef}", request.OdooRef);
            return StatusCode(500, ApiResponse<SupplierAdjustmentResult>.Fail(ex.Message));
        }
    }

    /// <summary>POST /api/supplier-adjustments/{jdtDocEntry}/reverse — SAP storno of the adjustment (idempotent).</summary>
    [HttpPost("supplier-adjustments/{jdtDocEntry:int}/reverse")]
    [ProducesResponseType(typeof(ApiResponse<AdjustmentReverseResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<AdjustmentReverseResult>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<AdjustmentReverseResult>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReverseSupplierAdjustment(int jdtDocEntry, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AdjustmentReverseRequest? request)
    {
        string reversalRef = $"{(request?.OdooRef ?? $"JE-{jdtDocEntry}").Trim()}:REV";
        if (reversalRef.Length > 50) reversalRef = reversalRef[..50];

        _logger.LogInformation("Supplier adjustment reversal requested: TransId={TransId}, ref={Ref}", jdtDocEntry, reversalRef);

        try
        {
            var result = await _sap.ReverseSupplierAdjustmentJournalAsync(jdtDocEntry, reversalRef);
            return Ok(ApiResponse<AdjustmentReverseResult>.Ok(result));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(ApiResponse<AdjustmentReverseResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            // SAP's refusal (period closed, already reversed by hand, …) comes back as a 409 so the job
            // fails without an automatic retry, as the spec asks for cancels and reversals.
            _logger.LogError(ex, "Supplier adjustment reversal failed: TransId={TransId}", jdtDocEntry);
            return Conflict(ApiResponse<AdjustmentReverseResult>.Fail(ex.Message));
        }
    }

    // ------------------------------------------------------------------

    private async Task<IActionResult> CreatePaymentAsync(SupplierPaymentRequest request, bool isRefund)
    {
        string label = isRefund ? "Supplier refund" : "Outgoing payment";

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.OdooPayRef)) errors.Add("odoo_pay_ref is required.");
        else if (request.OdooPayRef.Trim().Length > 50) errors.Add("odoo_pay_ref must be at most 50 characters (U_OdooPayRef).");
        if (string.IsNullOrWhiteSpace(request.CardCode)) errors.Add("card_code is required.");
        if (string.IsNullOrWhiteSpace(request.SapAccountCode)) errors.Add("sap_account_code (the SAP bank G/L) is required.");
        if (request.DocDate == default) errors.Add("doc_date is required.");
        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Trim().Length != 3) errors.Add("currency must be a 3-letter ISO code.");
        if (request.Amount <= 0) errors.Add("amount must be greater than zero.");
        if (request.Rate < 0) errors.Add("rate cannot be negative.");
        if (errors.Count > 0)
            return BadRequest(ApiResponse<SupplierPaymentResult>.Fail(errors));

        request.IsRefund = isRefund;
        request.OdooPayRef = request.OdooPayRef.Trim();
        request.CardCode = request.CardCode.Trim();
        request.Currency = request.Currency.Trim().ToUpperInvariant();
        request.SapAccountCode = request.SapAccountCode.Trim();

        _logger.LogInformation(
            "{Label} requested: ref={OdooPayRef}, card={CardCode}, {Amount} {Currency} @ {Rate}, bank G/L {Account}",
            label, request.OdooPayRef, request.CardCode, request.Amount, request.Currency, request.Rate, request.SapAccountCode);

        try
        {
            var result = await _sap.CreateSupplierPaymentOnAccountAsync(request);
            return Ok(ApiResponse<SupplierPaymentResult>.Ok(result));
        }
        catch (SapConflictException ex)
        {
            _logger.LogWarning("{Label} conflict: ref={OdooPayRef}: {Message}", label, request.OdooPayRef, ex.Message);
            return Conflict(ApiResponse<SupplierPaymentResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Label} failed: ref={OdooPayRef}", label, request.OdooPayRef);
            return StatusCode(500, ApiResponse<SupplierPaymentResult>.Fail(ex.Message));
        }
    }

    private async Task<IActionResult> CancelPaymentAsync(int docEntry, SupplierPaymentCancelRequest? request, bool isRefund)
    {
        string label = isRefund ? "Supplier refund" : "Outgoing payment";
        _logger.LogInformation("{Label} cancellation requested: DocEntry={DocEntry}, ref={Ref}, reason={Reason}",
            label, docEntry, request?.OdooPayRef, request?.Reason);

        try
        {
            var sap = await _sap.CancelSupplierPaymentAsync(docEntry, isRefund);
            return Ok(ApiResponse<SupplierPaymentCancelResult>.Ok(new SupplierPaymentCancelResult
            {
                DocEntry = sap.DocEntry,
                DocNum = sap.DocNum,
                Cancelled = true,
                AlreadyCancelled = sap.AlreadyCancelled,
            }));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(ApiResponse<SupplierPaymentCancelResult>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            // SAP's refusal flows through verbatim as a 409: the job fails and is not retried automatically.
            _logger.LogError(ex, "{Label} cancellation failed: DocEntry={DocEntry}", label, docEntry);
            return Conflict(ApiResponse<SupplierPaymentCancelResult>.Fail(ex.Message));
        }
    }
}
