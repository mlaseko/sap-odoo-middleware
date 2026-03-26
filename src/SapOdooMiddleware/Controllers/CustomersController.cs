using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Receives Customer (BusinessPartner) requests from Odoo and creates/updates
/// them in SAP B1 via DI API.
///
/// POST /api/customers            — create a new customer
/// PUT  /api/customers/{cardCode} — update an existing customer
/// </summary>
[ApiController]
[Route("api/customers")]
public class CustomersController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly ILogger<CustomersController> _logger;

    public CustomersController(ISapB1Service sapService, ILogger<CustomersController> logger)
    {
        _sapService = sapService;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/customers
    /// Creates a Customer (BusinessPartner CardType=C) in SAP B1.
    /// Returns the auto-generated CardCode so Odoo ICC can write it back
    /// to <c>x_sap_cardcode</c> on the <c>res.partner</c> record.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SapCustomerRequest request)
    {
        _logger.LogInformation(
            "Received customer creation request — OdooCustomerId={OdooCustomerId}, CardName={CardName}",
            request.OdooCustomerId, request.CardName);

        try
        {
            var result = await _sapService.CreateCustomerAsync(request);

            _logger.LogInformation(
                "SAP Customer created: CardCode={CardCode}, CardName={CardName}, OdooId={OdooId}",
                result.CardCode, result.CardName, result.OdooCustomerId);

            return Ok(ApiResponse<SapCustomerResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create SAP Customer for OdooCustomerId={OdooCustomerId}",
                request.OdooCustomerId);
            return StatusCode(500, ApiResponse<SapCustomerResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// PUT /api/customers/{cardCode}
    /// Updates an existing Customer in SAP B1.
    /// </summary>
    [HttpPut("{cardCode}")]
    public async Task<IActionResult> Update(string cardCode, [FromBody] SapCustomerRequest request)
    {
        _logger.LogInformation(
            "Received customer update request — CardCode={CardCode}, CardName={CardName}, OdooCustomerId={OdooCustomerId}",
            cardCode, request.CardName, request.OdooCustomerId);

        try
        {
            var result = await _sapService.UpdateCustomerAsync(cardCode, request);

            _logger.LogInformation(
                "SAP Customer updated: CardCode={CardCode}, CardName={CardName}",
                result.CardCode, result.CardName);

            return Ok(ApiResponse<SapCustomerResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to update SAP Customer CardCode={CardCode}", cardCode);
            return StatusCode(500, ApiResponse<SapCustomerResponse>.Fail(ex.Message));
        }
    }
}
