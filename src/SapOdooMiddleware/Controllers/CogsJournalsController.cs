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
    private readonly ISapB1Service _sapService;
    private readonly ILogger<CogsJournalsController> _logger;

    public CogsJournalsController(
        IOdooService odooService,
        ISapB1Service sapService,
        ILogger<CogsJournalsController> logger)
    {
        _odooService = odooService;
        _sapService = sapService;
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

    /// <summary>
    /// POST /api/cogs-journals/from-sap/{docEntry}
    /// Reads line-level cost data (GrossBuyPrice) directly from the SAP AR
    /// Invoice and creates the COGS journal entry in Odoo.
    ///
    /// Use this when the COGS was missed during the original invoice sync —
    /// the caller only needs to supply the SAP DocEntry; costs are read live
    /// from SAP B1 via the DI API.
    /// </summary>
    [HttpPost("from-sap/{docEntry:int}")]
    public async Task<IActionResult> CreateFromSap(int docEntry)
    {
        _logger.LogInformation(
            "COGS from-SAP request — reading invoice costs for DocEntry={DocEntry}",
            docEntry);

        try
        {
            // Step 1: Read invoice line costs from SAP B1
            var invoiceData = await _sapService.ReadInvoiceCostsAsync(docEntry);

            if (invoiceData.Lines.Count == 0)
            {
                _logger.LogInformation(
                    "No lines with cost data in SAP invoice DocEntry={DocEntry} — skipping",
                    docEntry);

                return Ok(ApiResponse<CogsJournalResponse>.Ok(new CogsJournalResponse
                {
                    SapDocEntry = docEntry,
                    Action = "skipped",
                }));
            }

            // Step 2: Build COGS request from SAP data
            var cogsRequest = new CogsJournalRequest
            {
                DocEntry = invoiceData.DocEntry,
                DocNum = invoiceData.DocNum,
                Lines = invoiceData.Lines.Select(l => new CogsJournalLineRequest
                {
                    LineNum = l.LineNum,
                    ItemCode = l.ItemCode,
                    Quantity = l.Quantity,
                    UnitCost = l.GrossBuyPrice,
                }).ToList(),
            };

            // Step 3: Create COGS journal entry in Odoo
            var result = await _odooService.CreateOrUpdateCogsJournalAsync(cogsRequest);

            _logger.LogInformation(
                "COGS from-SAP {Action}: DocEntry={DocEntry}, JeId={JeId}, TotalCogs={TotalCogs}",
                result.Action, docEntry, result.CogsJournalEntryId, result.TotalCogs);

            return Ok(ApiResponse<CogsJournalResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "COGS from-SAP failed for DocEntry={DocEntry}", docEntry);

            return StatusCode(500, ApiResponse<CogsJournalResponse>.Fail(ex.Message));
        }
    }
}
