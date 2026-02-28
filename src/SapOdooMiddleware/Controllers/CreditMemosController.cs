using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Receives AR Credit Memo requests from Odoo and creates them in SAP B1 via DI API.
/// After successful creation, writes SAP DocEntry back to the Odoo credit note
/// when <c>odoo_invoice_id</c> is provided in the request.
/// </summary>
[ApiController]
[Route("api/credit-memos")]
public class CreditMemosController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly IOdooService _odooService;
    private readonly ILogger<CreditMemosController> _logger;

    public CreditMemosController(
        ISapB1Service sapService,
        IOdooService odooService,
        ILogger<CreditMemosController> logger)
    {
        _sapService = sapService;
        _odooService = odooService;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/credit-memos
    /// Creates an AR Credit Memo (ORIN) in SAP B1, optionally by copying from the
    /// original AR Invoice (OINV).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SapCreditMemoRequest request)
    {
        _logger.LogInformation(
            "Received Credit Memo creation request — ExternalCreditMemoId={ExternalCreditMemoId}, " +
            "CustomerCode={CustomerCode}, SapBaseInvoiceDocEntry={SapBaseInvoiceDocEntry}, " +
            "OdooInvoiceId={OdooInvoiceId}, LineCount={LineCount}",
            request.ExternalCreditMemoId,
            request.CustomerCode,
            request.SapBaseInvoiceDocEntry,
            request.OdooInvoiceId,
            request.Lines.Count);

        try
        {
            var result = await _sapService.CreateCreditMemoAsync(request);

            _logger.LogInformation(
                "SAP Credit Memo created: DocEntry={DocEntry}, DocNum={DocNum}, " +
                "ExternalCreditMemoId={ExternalCreditMemoId}",
                result.DocEntry, result.DocNum, result.ExternalCreditMemoId);

            // Write back SAP fields to Odoo
            if (request.OdooInvoiceId.HasValue && request.OdooInvoiceId.Value > 0)
            {
                await WriteBackToOdoo(request.OdooInvoiceId.Value, result);
            }

            return Ok(ApiResponse<SapCreditMemoResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create SAP Credit Memo for ExternalCreditMemoId={ExternalCreditMemoId}",
                request.ExternalCreditMemoId);

            return StatusCode(500, ApiResponse<SapCreditMemoResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// PUT /api/credit-memos/{docEntry}
    /// Updates UDF fields on an existing AR Credit Memo in SAP B1 (re-sync).
    /// </summary>
    [HttpPut("{docEntry:int}")]
    public async Task<IActionResult> Update(int docEntry, [FromBody] SapCreditMemoRequest request)
    {
        _logger.LogInformation(
            "Received Credit Memo update request — DocEntry={DocEntry}, ExternalCreditMemoId={ExternalCreditMemoId}",
            docEntry, request.ExternalCreditMemoId);

        try
        {
            var result = await _sapService.UpdateCreditMemoAsync(docEntry, request);

            if (request.OdooInvoiceId.HasValue && request.OdooInvoiceId.Value > 0)
            {
                await WriteBackToOdoo(request.OdooInvoiceId.Value, result);
            }

            return Ok(ApiResponse<SapCreditMemoResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update SAP Credit Memo DocEntry={DocEntry}", docEntry);
            return StatusCode(500, ApiResponse<SapCreditMemoResponse>.Fail(ex.Message));
        }
    }

    private async Task WriteBackToOdoo(int odooInvoiceId, SapCreditMemoResponse result)
    {
        try
        {
            _logger.LogInformation(
                "Starting Odoo write-back — OdooInvoiceId={OdooInvoiceId}, SapDocEntry={SapDocEntry}",
                odooInvoiceId, result.DocEntry);

            await _odooService.UpdateCreditMemoAsync(new CreditMemoWriteBackRequest
            {
                OdooInvoiceId = odooInvoiceId,
                SapDocEntry = result.DocEntry
            });

            result.OdooWriteBackSuccess = true;

            _logger.LogInformation(
                "Odoo write-back completed — OdooInvoiceId={OdooInvoiceId}, SapDocEntry={SapDocEntry}",
                odooInvoiceId, result.DocEntry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Odoo write-back failed for OdooInvoiceId={OdooInvoiceId}. " +
                "SAP Credit Memo was created successfully — manual update may be needed.",
                odooInvoiceId);

            result.OdooWriteBackSuccess = false;
            result.OdooWriteBackError = ex.Message;
        }
    }
}
