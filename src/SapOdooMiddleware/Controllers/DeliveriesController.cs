using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Receives delivery confirmations from SAP B1 and updates Odoo stock.picking.
/// </summary>
[ApiController]
[Route("api/deliveries")]
public class DeliveriesController : ControllerBase
{
    private readonly IOdooService _odooService;
    private readonly IDeliveryMonitorService _monitor;
    private readonly ILogger<DeliveriesController> _logger;

    public DeliveriesController(
        IOdooService odooService,
        IDeliveryMonitorService monitor,
        ILogger<DeliveriesController> logger)
    {
        _odooService = odooService;
        _monitor = monitor;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/deliveries
    /// Confirms a delivery in Odoo after SAP Delivery Note is posted.
    /// Header-only payload: {odoo_so_ref, sap_delivery_no, delivery_date, status}.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DeliveryUpdateRequest request)
    {
        _logger.LogInformation(
            "Received delivery update: UOdooSoId={UOdooSoId}, SapDeliveryNo={SapDeliveryNo}",
            request.ResolvedSoId, request.SapDeliveryNo);

        var stopwatch = Stopwatch.StartNew();

        // Notify monitor: processing
        await _monitor.NotifyAsync(new DeliveryMonitorPayload
        {
            Source = "api",
            OdooSoId = request.ResolvedSoId,
            SapDeliveryNo = request.SapDeliveryNo,
            DeliveryDate = request.DeliveryDate?.ToString("yyyy-MM-dd HH:mm:ss"),
            State = "processing",
        });

        try
        {
            var result = await _odooService.ConfirmDeliveryAsync(request);
            stopwatch.Stop();

            _logger.LogInformation(
                "Odoo delivery confirmed: PickingId={PickingId}, Name={Name}, State={State}",
                result.PickingId, result.PickingName, result.State);

            // Notify monitor: done
            await _monitor.NotifyAsync(new DeliveryMonitorPayload
            {
                Source = "api",
                OdooSoId = request.ResolvedSoId,
                SapDeliveryNo = request.SapDeliveryNo,
                DeliveryDate = request.DeliveryDate?.ToString("yyyy-MM-dd HH:mm:ss"),
                State = "done",
                PickingId = result.PickingId,
                PickingName = result.PickingName,
                PickingState = result.State,
                ProcessedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                Duration = stopwatch.Elapsed.TotalSeconds,
            });

            return Ok(ApiResponse<DeliveryUpdateResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            _logger.LogError(ex, "Failed to confirm delivery in Odoo for ref {UOdooSoId}", request.ResolvedSoId);

            // Notify monitor: failed
            await _monitor.NotifyAsync(new DeliveryMonitorPayload
            {
                Source = "api",
                OdooSoId = request.ResolvedSoId,
                SapDeliveryNo = request.SapDeliveryNo,
                DeliveryDate = request.DeliveryDate?.ToString("yyyy-MM-dd HH:mm:ss"),
                State = "failed",
                ErrorMessage = ex.Message,
                ProcessedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                Duration = stopwatch.Elapsed.TotalSeconds,
            });

            return StatusCode(500, ApiResponse<DeliveryUpdateResponse>.Fail(ex.Message));
        }
    }
}
