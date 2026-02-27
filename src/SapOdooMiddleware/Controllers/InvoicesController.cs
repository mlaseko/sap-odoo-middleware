using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Receives AR Invoice requests from Odoo and creates them in SAP B1 via DI API.
/// After successful creation, writes SAP DocEntry and per-line data (LineNum, GrossBuyPrice)
/// back to the Odoo invoice when <c>odoo_invoice_id</c> is provided in the request.
/// Then automatically creates the COGS journal entry using the same cost data.
/// </summary>
[ApiController]
[Route("api/invoices")]
public class InvoicesController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly IOdooService _odooService;
    private readonly ILogger<InvoicesController> _logger;

    public InvoicesController(
        ISapB1Service sapService,
        IOdooService odooService,
        ILogger<InvoicesController> logger)
    {
        _sapService = sapService;
        _odooService = odooService;
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
    ///
    /// When <c>odoo_invoice_id</c> is provided, the middleware writes back to Odoo:
    /// - <c>x_sap_invoice_docentry</c> on the account.move header
    /// - <c>x_sap_invoice_linenum</c> and <c>x_sap_gross_buy_price</c> on each invoice line
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SapInvoiceRequest request)
    {
        _logger.LogInformation(
            "Received AR Invoice creation request — ExternalInvoiceId={ExternalInvoiceId}, " +
            "CustomerCode={CustomerCode}, CopyFromDelivery={CopyFromDelivery}, " +
            "SapDeliveryDocEntry={SapDeliveryDocEntry}, SapSalesOrderDocEntry={SapSalesOrderDocEntry}, " +
            "OdooInvoiceId={OdooInvoiceId}, LineCount={LineCount}",
            request.ExternalInvoiceId,
            request.CustomerCode,
            request.CopyFromDelivery,
            request.SapDeliveryDocEntry,
            request.SapSalesOrderDocEntry,
            request.OdooInvoiceId,
            request.Lines.Count);

        try
        {
            // Step 1: Create the AR Invoice in SAP B1
            var result = await _sapService.CreateInvoiceAsync(request);

            _logger.LogInformation(
                "SAP AR Invoice created: DocEntry={DocEntry}, DocNum={DocNum}, " +
                "ExternalInvoiceId={ExternalInvoiceId}, BaseDeliveryDocEntry={BaseDeliveryDocEntry}, " +
                "BaseSalesOrderDocEntry={BaseSalesOrderDocEntry}, LineCount={LineCount}",
                result.DocEntry,
                result.DocNum,
                result.ExternalInvoiceId,
                result.BaseDeliveryDocEntry,
                result.BaseSalesOrderDocEntry,
                result.Lines.Count);

            // Step 2: Write back SAP fields to Odoo (when OdooInvoiceId is provided)
            if (request.OdooInvoiceId.HasValue && request.OdooInvoiceId.Value > 0)
            {
                await WriteBackToOdoo(request.OdooInvoiceId.Value, result);

                // Step 3: Create COGS journal entry using the same cost data
                await CreateCogsJournal(result);
            }
            else
            {
                _logger.LogInformation(
                    "Skipping Odoo write-back and COGS — OdooInvoiceId not provided in request.");
            }

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

    /// <summary>
    /// PUT /api/invoices/{docEntry}
    /// Updates UDF fields on an existing AR Invoice in SAP B1 (re-sync).
    /// </summary>
    [HttpPut("{docEntry:int}")]
    public async Task<IActionResult> Update(int docEntry, [FromBody] SapInvoiceRequest request)
    {
        _logger.LogInformation(
            "Received AR Invoice update request — DocEntry={DocEntry}, ExternalInvoiceId={ExternalInvoiceId}",
            docEntry, request.ExternalInvoiceId);

        try
        {
            var result = await _sapService.UpdateInvoiceAsync(docEntry, request);

            _logger.LogInformation(
                "SAP AR Invoice updated: DocEntry={DocEntry}, DocNum={DocNum}",
                result.DocEntry, result.DocNum);

            // Write back to Odoo if OdooInvoiceId is provided
            if (request.OdooInvoiceId.HasValue && request.OdooInvoiceId.Value > 0)
            {
                await WriteBackToOdoo(request.OdooInvoiceId.Value, result);
            }

            return Ok(ApiResponse<SapInvoiceResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update SAP AR Invoice DocEntry={DocEntry}", docEntry);
            return StatusCode(500, ApiResponse<SapInvoiceResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Writes SAP invoice DocEntry + per-line data back to Odoo.
    /// Failures are logged and attached to the response but do NOT fail the overall request
    /// (the SAP invoice was already created successfully).
    /// </summary>
    private async Task WriteBackToOdoo(int odooInvoiceId, SapInvoiceResponse result)
    {
        try
        {
            _logger.LogInformation(
                "Starting Odoo write-back — OdooInvoiceId={OdooInvoiceId}, SapDocEntry={SapDocEntry}, SapLineCount={LineCount}",
                odooInvoiceId, result.DocEntry, result.Lines.Count);

            var writeBackRequest = new InvoiceWriteBackRequest
            {
                OdooInvoiceId = odooInvoiceId,
                SapDocEntry = result.DocEntry,
                Lines = result.Lines.Select(l => new InvoiceLineWriteBack
                {
                    SapLineNum = l.LineNum,
                    GrossBuyPrice = l.GrossBuyPrice
                }).ToList()
            };

            var writeBackResult = await _odooService.UpdateInvoiceSapFieldsAsync(writeBackRequest);

            result.OdooWriteBackSuccess = writeBackResult.Success;

            _logger.LogInformation(
                "Odoo write-back completed — OdooInvoiceId={OdooInvoiceId}, LinesUpdated={LinesUpdated}, Success={Success}",
                writeBackResult.OdooInvoiceId,
                writeBackResult.LinesUpdated,
                writeBackResult.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Odoo write-back failed for OdooInvoiceId={OdooInvoiceId}, SapDocEntry={SapDocEntry}. " +
                "SAP invoice was created successfully — manual reconciliation may be needed.",
                odooInvoiceId, result.DocEntry);

            result.OdooWriteBackSuccess = false;
            result.OdooWriteBackError = ex.Message;
        }
    }

    /// <summary>
    /// Creates the COGS journal entry in Odoo using cost data from the SAP invoice response.
    /// Converts GrossBuyPrice per line into UnitCost for the COGS request.
    /// Failures are logged but do NOT fail the overall request.
    /// </summary>
    private async Task CreateCogsJournal(SapInvoiceResponse result)
    {
        if (result.Lines.Count == 0)
        {
            _logger.LogInformation(
                "Skipping COGS journal — no lines with cost data in SAP response for DocEntry={DocEntry}",
                result.DocEntry);
            return;
        }

        try
        {
            _logger.LogInformation(
                "Starting COGS journal creation — SapDocEntry={SapDocEntry}, LineCount={LineCount}",
                result.DocEntry, result.Lines.Count);

            var cogsRequest = new CogsJournalRequest
            {
                DocEntry = result.DocEntry,
                DocNum = result.DocNum,
                Lines = result.Lines.Select(l => new CogsJournalLineRequest
                {
                    LineNum = l.LineNum,
                    ItemCode = l.ItemCode,
                    Quantity = l.Quantity,
                    UnitCost = l.GrossBuyPrice
                }).ToList()
            };

            var cogsResult = await _odooService.CreateOrUpdateCogsJournalAsync(cogsRequest);

            result.CogsJournalAction = cogsResult.Action;
            result.CogsJournalEntryId = cogsResult.CogsJournalEntryId;

            _logger.LogInformation(
                "COGS journal {Action} — JeId={JeId}, TotalCogs={TotalCogs}, Hash={Hash}",
                cogsResult.Action, cogsResult.CogsJournalEntryId,
                cogsResult.TotalCogs, cogsResult.Hash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "COGS journal creation failed for SapDocEntry={SapDocEntry}. " +
                "Invoice and write-back succeeded — use POST /api/cogs-journals to retry.",
                result.DocEntry);

            result.CogsJournalAction = "failed";
            result.CogsJournalError = ex.Message;
        }
    }
}
