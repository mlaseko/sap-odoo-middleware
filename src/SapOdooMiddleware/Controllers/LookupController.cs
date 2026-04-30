using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Looks up SAP documents by their Odoo reference (stored in UDFs).
/// Used by the ICC "SAP Field Sync" page to find SAP DocEntry/DocNum
/// for Odoo documents that are missing SAP identifiers.
/// </summary>
[ApiController]
[Route("api/lookup")]
public class LookupController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly ILogger<LookupController> _logger;

    public LookupController(
        ISapB1Service sapService,
        ILogger<LookupController> logger)
    {
        _sapService = sapService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/lookup/{documentType}?odoo_ref={odooRef}
    /// Searches SAP for a document matching the Odoo reference in UDFs.
    ///
    /// Supported document types:
    ///   sales-order, delivery, invoice, payment, return, credit-memo, customer
    ///
    /// Returns the SAP DocEntry, DocNum, status, and PickListEntry (for SO/delivery).
    /// For <c>customer</c> the lookup is keyed by OCRD.U_OdooCustomerId and
    /// returns CardCode (DocEntry/DocNum default to 0).
    /// Returns 404 if no matching document is found.
    /// </summary>
    [HttpGet("{documentType}")]
    public async Task<IActionResult> Lookup(
        string documentType,
        [FromQuery(Name = "odoo_ref")] string odooRef)
    {
        if (string.IsNullOrWhiteSpace(odooRef))
            return BadRequest(ApiResponse<SapDocumentLookupResponse>.Fail(
                "Query parameter 'odoo_ref' is required."));

        _logger.LogInformation(
            "Lookup request — type={DocumentType}, odoo_ref={OdooRef}",
            documentType, odooRef);

        try
        {
            var result = await _sapService.LookupDocumentAsync(
                documentType, odooRef.Trim());

            if (result == null)
            {
                return NotFound(ApiResponse<SapDocumentLookupResponse>.Fail(
                    $"No SAP {documentType} found with Odoo reference '{odooRef}'."));
            }

            return Ok(ApiResponse<SapDocumentLookupResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Lookup failed — type={DocumentType}, odoo_ref={OdooRef}",
                documentType, odooRef);

            return StatusCode(500,
                ApiResponse<SapDocumentLookupResponse>.Fail(ex.Message));
        }
    }
}
