using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Standalone endpoint for creating COGS journal entries in Odoo.
/// Use this for re-runs, retries, or batch processing.
/// COGS is also triggered automatically during the invoice write-back flow
/// (POST /api/invoices with odoo_invoice_id).
/// </summary>
[ApiController]
[Route("api/cogs-journals")]
public class CogsJournalsController : ControllerBase
{
    private readonly IOdooService _odooService;
    private readonly ILogger<CogsJournalsController> _logger;

    public CogsJournalsController(
        IOdooService odooService,
        ILogger<CogsJournalsController> logger)
    {
        _odooService = odooService;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/cogs-journals
    /// Creates or updates a COGS journal entry in Odoo for a SAP AR Invoice.
    ///
    /// Flow: find Odoo invoice by DocEntry → match lines → compute COGS →
    /// build JE (debit per line + 1 credit) → hash check → create/update → post.
    ///
    /// Idempotent: if a COGS JE already exists with the same hash, it is skipped.
    /// If the hash differs, the existing JE is updated (draft → replace lines → repost).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CogsJournalRequest request)
    {
        _logger.LogInformation(
            "Received COGS journal request — DocEntry={DocEntry}, DocNum={DocNum}, LineCount={LineCount}",
            request.DocEntry, request.DocNum, request.Lines.Count);

        try
        {
            var result = await _odooService.CreateOrUpdateCogsJournalAsync(request);

            _logger.LogInformation(
                "COGS journal {Action}: JeId={JeId}, InvoiceId={InvoiceId}, InvoiceName={InvoiceName}, " +
                "TotalCogs={TotalCogs}, Hash={Hash}",
                result.Action, result.CogsJournalEntryId, result.OdooInvoiceId,
                result.OdooInvoiceName, result.TotalCogs, result.Hash);

            return Ok(ApiResponse<CogsJournalResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create COGS journal entry for SAP DocEntry={DocEntry}",
                request.DocEntry);

            return StatusCode(500, ApiResponse<CogsJournalResponse>.Fail(ex.Message));
        }
    }
}
