using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Unified re-sync endpoint for updating existing SAP documents with the latest
/// Odoo payload.  Used when the initial creation succeeded but some fields
/// (UDFs, amounts, account mappings) were missing or incorrect.
///
/// The caller selects a <c>document_type</c> and provides the SAP <c>doc_entry</c>.
/// The endpoint routes to the appropriate update service method, re-applies
/// all fields from the request body, and optionally writes back to Odoo.
///
/// Each re-sync operation is logged as an entry in the ODOO_WEBHOOK_QUEUE table
/// with EventType = 'resync' so it appears in the monitoring dashboard.
/// </summary>
[ApiController]
[Route("api/resync")]
public class ResyncController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly IOdooService _odooService;
    private readonly IOptionsSnapshot<WebhookQueueSettings> _settings;
    private readonly ILogger<ResyncController> _logger;

    public ResyncController(
        ISapB1Service sapService,
        IOdooService odooService,
        IOptionsSnapshot<WebhookQueueSettings> settings,
        ILogger<ResyncController> logger)
    {
        _sapService = sapService;
        _odooService = odooService;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/resync
    /// Re-syncs a SAP document by updating it with a fresh payload from Odoo.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Resync([FromBody] ResyncRequest request)
    {
        _logger.LogInformation(
            "Re-sync request — DocumentType={DocumentType}, DocEntry={DocEntry}",
            request.DocumentType, request.DocEntry);

        // Resolve the OdooSoId from the sub-payload for traceability
        string odooSoId = ResolveOdooSoId(request);

        // Insert a queue entry to track this resync operation
        int? queueEntryId = await InsertResyncQueueEntryAsync(
            request.DocEntry, odooSoId, request.DocumentType);

        try
        {
            object result = request.DocumentType.ToLowerInvariant() switch
            {
                "sales_order" => await _sapService.UpdateSalesOrderAsync(
                    request.DocEntry, request.SalesOrder
                        ?? throw new ArgumentException("sales_order payload is required")),

                "invoice" => await _sapService.UpdateInvoiceAsync(
                    request.DocEntry, request.Invoice
                        ?? throw new ArgumentException("invoice payload is required")),

                "incoming_payment" => await _sapService.UpdateIncomingPaymentAsync(
                    request.DocEntry, request.IncomingPayment
                        ?? throw new ArgumentException("incoming_payment payload is required")),

                "credit_memo" => await _sapService.UpdateCreditMemoAsync(
                    request.DocEntry, request.CreditMemo
                        ?? throw new ArgumentException("credit_memo payload is required")),

                "goods_return" => await _sapService.UpdateGoodsReturnAsync(
                    request.DocEntry, request.GoodsReturn
                        ?? throw new ArgumentException("goods_return payload is required")),

                _ => throw new ArgumentException(
                    $"Unsupported document type: '{request.DocumentType}'. " +
                    "Supported types: sales_order, invoice, incoming_payment, credit_memo, goods_return")
            };

            _logger.LogInformation(
                "Re-sync completed — DocumentType={DocumentType}, DocEntry={DocEntry}",
                request.DocumentType, request.DocEntry);

            // When a payment reallocation occurred (cancel + recreate), the DocEntry
            // changed.  Write the new DocEntry/DocNum back to Odoo so the records
            // stay in sync regardless of whether the Odoo caller processes the response.
            if (result is SapIncomingPaymentResponse paymentResult
                && paymentResult.Reallocated
                && request.IncomingPayment?.OdooPaymentId is > 0)
            {
                await WriteBackPaymentToOdoo(
                    request.IncomingPayment.OdooPaymentId.Value, paymentResult);
            }

            string responseBody = System.Text.Json.JsonSerializer.Serialize(result);
            await MarkQueueEntryDoneAsync(queueEntryId, responseBody);

            return Ok(ApiResponse<object>.Ok(result));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Re-sync validation error");
            await MarkQueueEntryFailedAsync(queueEntryId, ex.Message);
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Re-sync failed — DocumentType={DocumentType}, DocEntry={DocEntry}",
                request.DocumentType, request.DocEntry);
            await MarkQueueEntryFailedAsync(queueEntryId, ex.Message);
            return StatusCode(500, ApiResponse<object>.Fail(ex.Message));
        }
    }

    // ------------------------------------------------------------------
    // Queue entry tracking helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Extracts the Odoo SO reference from the sub-payload for traceability.
    /// </summary>
    private static string ResolveOdooSoId(ResyncRequest request)
    {
        return request.DocumentType.ToLowerInvariant() switch
        {
            "sales_order" => request.SalesOrder?.UOdooSoId ?? string.Empty,
            "invoice" => request.Invoice?.UOdooSoId ?? string.Empty,
            "incoming_payment" => request.IncomingPayment?.UOdooSoId ?? string.Empty,
            "credit_memo" => request.CreditMemo?.UOdooSoId ?? string.Empty,
            "goods_return" => request.GoodsReturn?.UOdooSoId ?? string.Empty,
            _ => string.Empty
        };
    }

    /// <summary>
    /// Inserts a new queue entry with EventType = 'resync' and Status = 'processing'.
    /// Returns the new entry Id, or null if the insert fails (non-fatal).
    /// </summary>
    private async Task<int?> InsertResyncQueueEntryAsync(
        int docEntry, string odooSoId, string documentType)
    {
        var connectionString = _settings.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            // Ensure EventType column exists (same migration as WebhookQueueController)
            const string ensureSql = """
                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.columns
                    WHERE object_id = OBJECT_ID(N'[dbo].[ODOO_WEBHOOK_QUEUE]')
                      AND name = 'EventType'
                )
                ALTER TABLE [dbo].[ODOO_WEBHOOK_QUEUE]
                    ADD [EventType] NVARCHAR(50) NOT NULL
                        CONSTRAINT DF_ODOO_WEBHOOK_QUEUE_EventType DEFAULT 'webhook';
                """;
            using (var ensureCmd = new SqlCommand(ensureSql, connection))
            {
                await ensureCmd.ExecuteNonQueryAsync();
            }

            const string sql = """
                INSERT INTO [dbo].[ODOO_WEBHOOK_QUEUE]
                    ([DocEntry], [OdooSoId], [Status], [RetryCount], [CreatedAt], [EventType], [ErrorMessage])
                VALUES
                    (@DocEntry, @OdooSoId, 'processing', 0, GETDATE(), @EventType, @DocumentType);
                SELECT SCOPE_IDENTITY();
                """;

            using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@DocEntry", docEntry);
            cmd.Parameters.AddWithValue("@OdooSoId", (object?)odooSoId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@EventType", "resync");
            cmd.Parameters.AddWithValue("@DocumentType", $"resync:{documentType}");

            var result = await cmd.ExecuteScalarAsync();
            int id = Convert.ToInt32(result);

            _logger.LogInformation(
                "Resync queue entry created: Id={Id}, DocEntry={DocEntry}, Type={DocumentType}",
                id, docEntry, documentType);

            return id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not insert resync queue entry for DocEntry={DocEntry}. " +
                "The resync will still proceed.", docEntry);
            return null;
        }
    }

    /// <summary>
    /// Marks the resync queue entry as 'done' with a timestamp and response body.
    /// </summary>
    private async Task MarkQueueEntryDoneAsync(int? entryId, string responseBody)
    {
        if (entryId == null)
            return;

        var connectionString = _settings.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = """
                UPDATE [dbo].[ODOO_WEBHOOK_QUEUE]
                SET [Status] = 'done',
                    [ProcessedAt] = GETDATE(),
                    [ResponseBody] = @ResponseBody,
                    [ErrorMessage] = NULL
                WHERE [Id] = @Id
                """;

            using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@Id", entryId.Value);
            cmd.Parameters.AddWithValue("@ResponseBody", (object?)responseBody ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not mark resync queue entry {Id} as done.", entryId);
        }
    }

    /// <summary>
    /// Marks the resync queue entry as 'failed' with the error message.
    /// </summary>
    private async Task MarkQueueEntryFailedAsync(int? entryId, string errorMessage)
    {
        if (entryId == null)
            return;

        var connectionString = _settings.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = """
                UPDATE [dbo].[ODOO_WEBHOOK_QUEUE]
                SET [Status] = 'failed',
                    [ProcessedAt] = GETDATE(),
                    [ErrorMessage] = @ErrorMessage
                WHERE [Id] = @Id
                """;

            using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@Id", entryId.Value);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)errorMessage ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not mark resync queue entry {Id} as failed.", entryId);
        }
    }

    /// <summary>
    /// Writes the new SAP DocEntry/DocNum back to Odoo after a payment reallocation.
    /// Failures are logged but do not fail the overall resync (SAP is already updated).
    /// </summary>
    private async Task WriteBackPaymentToOdoo(
        int odooPaymentId, SapIncomingPaymentResponse result)
    {
        try
        {
            _logger.LogInformation(
                "Writing reallocated payment back to Odoo — OdooPaymentId={OdooPaymentId}, " +
                "OldDocEntry={OldDocEntry}, NewDocEntry={NewDocEntry}, NewDocNum={NewDocNum}",
                odooPaymentId, result.CancelledDocEntry, result.DocEntry, result.DocNum);

            await _odooService.UpdateIncomingPaymentAsync(new IncomingPaymentWriteBackRequest
            {
                OdooPaymentId = odooPaymentId,
                SapDocEntry = result.DocEntry,
                SapDocNum = result.DocNum
            });

            result.OdooWriteBackSuccess = true;

            _logger.LogInformation(
                "Odoo write-back completed for reallocated payment — " +
                "OdooPaymentId={OdooPaymentId}, NewDocEntry={NewDocEntry}",
                odooPaymentId, result.DocEntry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Odoo write-back failed for reallocated payment — " +
                "OdooPaymentId={OdooPaymentId}, NewDocEntry={NewDocEntry}. " +
                "SAP payment was reallocated successfully — manual update may be needed.",
                odooPaymentId, result.DocEntry);

            result.OdooWriteBackSuccess = false;
            result.OdooWriteBackError = ex.Message;
        }
    }
}
