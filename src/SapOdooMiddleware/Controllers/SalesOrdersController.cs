using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Receives Sales Orders from Odoo and creates them in SAP B1 via DI API.
/// </summary>
[ApiController]
[Route("api/sales-orders")]
public class SalesOrdersController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly IOptionsSnapshot<WebhookQueueSettings> _webhookSettings;
    private readonly ILogger<SalesOrdersController> _logger;

    public SalesOrdersController(
        ISapB1Service sapService,
        IOptionsSnapshot<WebhookQueueSettings> webhookSettings,
        ILogger<SalesOrdersController> logger)
    {
        _sapService = sapService;
        _webhookSettings = webhookSettings;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/sales-orders
    /// Creates a Sales Order in SAP B1 and optionally a Pick List.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SapSalesOrderRequest request)
    {
        var deliveryIdProvided = !string.IsNullOrEmpty(request.OdooDeliveryId);
        _logger.LogInformation(
            "Received SO creation request — ResolvedSoId={ResolvedSoId}, CardCode={CardCode}, LineCount={LineCount}, OdooDeliveryId={OdooDeliveryIdProvided} (length={OdooDeliveryIdLength})",
            request.ResolvedSoId, request.CardCode, request.Lines.Count,
            deliveryIdProvided ? request.OdooDeliveryId : "(not provided)",
            request.OdooDeliveryId?.Length ?? 0);

        try
        {
            var result = await _sapService.CreateSalesOrderAsync(request);

            _logger.LogInformation(
                "SAP SO created: DocEntry={DocEntry}, DocNum={DocNum}, PickList={PickList}, Warnings={WarningCount}",
                result.DocEntry, result.DocNum, result.PickListEntry, result.Warnings.Count);

            // Surface structured bin-shortfall warnings as individual
            // WRN log entries so operators can see them in Serilog
            // without decoding the response body.  The ICC side also
            // consumes result.Warnings on write-back.
            foreach (var w in result.Warnings)
            {
                _logger.LogWarning(
                    "SO {DocEntry} warning [{Code}] item {ItemCode} line {LineNum} " +
                    "warehouse {Whs} required={Required} allocated={Allocated}: {Message}",
                    result.DocEntry, w.Code, w.ItemCode, w.LineNum, w.WarehouseCode,
                    w.Required, w.Allocated, w.Message);
            }

            return Ok(ApiResponse<SapSalesOrderResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create SAP Sales Order for Odoo ref {UOdooSoId}", request.ResolvedSoId);
            return StatusCode(500, ApiResponse<SapSalesOrderResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// PUT /api/sales-orders/{docEntry}
    /// Updates an existing Sales Order in SAP B1, refreshing sync UDFs.
    /// </summary>
    [HttpPut("{docEntry:int}")]
    public async Task<IActionResult> Update(int docEntry, [FromBody] SapSalesOrderRequest request)
    {
        _logger.LogInformation(
            "Received SO update request — DocEntry={DocEntry}, ResolvedSoId={ResolvedSoId}, CardCode={CardCode}",
            docEntry, request.ResolvedSoId, request.CardCode);

        try
        {
            var result = await _sapService.UpdateSalesOrderAsync(docEntry, request);

            _logger.LogInformation(
                "SAP SO updated: DocEntry={DocEntry}, DocNum={DocNum}",
                result.DocEntry, result.DocNum);

            // Post-update: if lines were changed, verify that the pick list
            // for this SO is still Released.  SAP B1's Close() + re-add
            // pattern (see SapB1DiApiService._RefreshPickListForSO) only
            // works when the original pick list is Released.  When the
            // warehouse has already picked or closed the pick list, the
            // service silently leaves it alone — meaning any new SO lines
            // land on ORDR/RDR1 but NOT on OPKL/PKL1.  Warehouse staff
            // then work from a stale pick list that is missing the new
            // items.  We detect that drift here and surface it as a
            // Warning on the response, which the ICC logs to
            // integration.log so operators can intervene.
            //
            // Query rationale:
            //   * Latest OPKL row for this SO = highest AbsEntry in PKL1
            //     joined by OrderEntry.  After a successful refresh there
            //     is a freshly-added OPKL row with Status = 'R'.  After
            //     a skipped refresh the latest row is still the original
            //     with Status = 'P' (Picked) or 'C' (Closed).
            //   * We only warn when the request included lines, because
            //     header-only updates do not touch the pick list.
            if (request.Lines is { Count: > 0 })
            {
                try
                {
                    var warning = await CheckPickListStatusForSoAsync(docEntry);
                    if (!string.IsNullOrEmpty(warning))
                    {
                        _logger.LogWarning(
                            "SO DocEntry={DocEntry} pick-list drift detected after line update: {Warning}",
                            docEntry, warning);
                        result.Warning = string.IsNullOrEmpty(result.Warning)
                            ? warning
                            : result.Warning + " | " + warning;
                    }
                }
                catch (Exception checkEx)
                {
                    _logger.LogWarning(checkEx,
                        "Post-update pick-list status check failed for SO DocEntry={DocEntry}. " +
                        "The SO update itself succeeded; only the drift-detection warning is missing.",
                        docEntry);
                }
            }

            return Ok(ApiResponse<SapSalesOrderResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update SAP Sales Order DocEntry={DocEntry}", docEntry);
            return StatusCode(500, ApiResponse<SapSalesOrderResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Returns a non-null warning string when the latest pick list for
    /// <paramref name="soDocEntry"/> is Picked ('P') or Closed ('C') —
    /// i.e. the pick list could not be refreshed with the SO's updated
    /// line set and the warehouse is working from stale data.  Returns
    /// <c>null</c> when the latest pick list is Released ('R'), or when
    /// no pick list exists for this SO (nothing to warn about), or when
    /// the WebhookQueue connection string is not configured (the check
    /// is best-effort and must not prevent a successful SO update from
    /// being returned to the caller).
    /// </summary>
    private async Task<string?> CheckPickListStatusForSoAsync(int soDocEntry)
    {
        var connectionString = _webhookSettings.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // SAP SQL Server access not configured — best-effort check is
            // unavailable.  The SO update itself has already succeeded;
            // we simply cannot detect pick-list drift from the middleware
            // side.  The Odoo-side line-count sanity check in
            // integration_job_line_drift.py still catches most drift cases.
            return null;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        const string sql = @"
            SELECT TOP (1) P.[AbsEntry], P.[Status]
            FROM   [dbo].[OPKL] P
            INNER  JOIN [dbo].[PKL1] L ON L.[AbsEntry] = P.[AbsEntry]
            WHERE  L.[BaseObject] = 17
              AND  L.[OrderEntry] = @SoDocEntry
            ORDER  BY P.[AbsEntry] DESC";

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@SoDocEntry", soDocEntry);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            // No pick list exists for this SO — nothing to drift from.
            return null;
        }

        int pklEntry = reader.GetInt32(0);
        string status = reader.IsDBNull(1) ? "N" : reader.GetString(1);

        if (status == "R")
        {
            // Released — pick list was refreshed successfully (or was
            // originally Released and left untouched because no lines
            // changed shape).
            return null;
        }

        string statusLabel = status switch
        {
            "P" => "Picked",
            "C" => "Closed",
            "Y" => "Closed",  // some SAP B1 versions report 'Y' for fully delivered
            _ => status,
        };

        return
            $"Pick list AbsEntry={pklEntry} for SO DocEntry={soDocEntry} is " +
            $"{statusLabel} ({status}) — SO lines were updated in SAP but the " +
            "pick list could NOT be refreshed with the new line set. Warehouse " +
            "must add missing line(s) manually to the active pick list, or a new " +
            "pick list must be created for the additional items.";
    }
}
