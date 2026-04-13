using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Odoo;

namespace SapOdooMiddleware.Services;

/// <summary>
/// Odoo JSON-RPC client that confirms deliveries by programmatically running
/// the standard stock.picking workflow: reserve → set quantities → validate.
/// </summary>
public class OdooJsonRpcService : IOdooService
{
    private readonly OdooSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OdooJsonRpcService> _logger;

    private int? _uid;
    private int _rpcId;

    public OdooJsonRpcService(
        IOptions<OdooSettings> settings,
        HttpClient httpClient,
        ILogger<OdooJsonRpcService> logger)
    {
        _settings = settings.Value;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<DeliveryUpdateResponse> ConfirmDeliveryAsync(DeliveryUpdateRequest request)
    {
        if (!_settings.UseBearerAuth)
            await EnsureAuthenticatedAsync();

        var soId = request.ResolvedSoId;

        // 1. Find sale.order by name
        var soIds = await SearchAsync("sale.order", new JsonArray
        {
            new JsonArray { JsonValue.Create("name"), JsonValue.Create("="), JsonValue.Create(soId) }
        });

        if (soIds.Count == 0)
            throw new InvalidOperationException($"Sale order '{soId}' not found in Odoo.");

        int soDbId = soIds[0];
        _logger.LogInformation("Found Odoo sale.order id={SoId} for ref={UOdooSoId}", soDbId, soId);

        // 2. Find related outgoing stock.picking that is not done/cancelled
        var pickingIds = await SearchAsync("stock.picking", new JsonArray
        {
            new JsonArray { JsonValue.Create("sale_id"), JsonValue.Create("="), JsonValue.Create(soDbId) },
            new JsonArray { JsonValue.Create("picking_type_code"), JsonValue.Create("="), JsonValue.Create("outgoing") },
            new JsonArray { JsonValue.Create("state"), JsonValue.Create("not in"), new JsonArray { JsonValue.Create("done"), JsonValue.Create("cancel") } }
        });

        if (pickingIds.Count == 0)
            throw new InvalidOperationException(
                $"No pending outgoing picking found for sale order '{soId}'.");

        int pickingId = pickingIds[0];
        _logger.LogInformation("Found Odoo stock.picking id={PickingId} for SO ref={UOdooSoId}", pickingId, soId);

        // 3. action_assign() — reserve stock
        await ExecuteMethodAsync("stock.picking", "action_assign", new JsonArray { pickingId });
        _logger.LogInformation("Reserved stock for picking id={PickingId}", pickingId);

        // 4. Read SAP delivery to get actual delivered items + quantities
        //    Then set qty_done only for the items that were delivered.
        var deliveredItems = request.DeliveredItems;

        var moveLineIds = await SearchAsync("stock.move.line", new JsonArray
        {
            new JsonArray { JsonValue.Create("picking_id"), JsonValue.Create("="), JsonValue.Create(pickingId) }
        });

        if (moveLineIds.Count > 0)
        {
            foreach (var mlId in moveLineIds)
            {
                var mlData = await ReadAsync("stock.move.line", mlId, new JsonArray
                {
                    JsonValue.Create("quantity"),
                    JsonValue.Create("product_id")
                });

                var demandQty = mlData?["quantity"]?.GetValue<double>() ?? 0;
                double qtyToSet = demandQty; // default: deliver full demand

                // If we have SAP delivery line data, match by product
                if (deliveredItems != null && deliveredItems.Count > 0)
                {
                    int productId = 0;
                    if (mlData?["product_id"] is JsonArray pArr && pArr.Count >= 1)
                        productId = pArr[0]!.GetValue<int>();

                    // Look up the product's SAP item code
                    string itemCode = "";
                    if (productId > 0)
                    {
                        var prodData = await ReadAsync("product.product", productId, new JsonArray
                        {
                            JsonValue.Create("x_sap_item_code"),
                            JsonValue.Create("default_code")
                        });
                        itemCode = prodData?["x_sap_item_code"]?.GetValue<string>()
                            ?? prodData?["default_code"]?.GetValue<string>() ?? "";
                    }

                    // Find matching delivered item
                    var match = deliveredItems.FirstOrDefault(d =>
                        string.Equals(d.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase));

                    if (match != null && match.Quantity > 0)
                    {
                        qtyToSet = Math.Min(match.Quantity, demandQty);
                        match.Quantity -= qtyToSet; // consume
                    }
                    else
                    {
                        qtyToSet = 0; // not delivered in this SAP delivery
                    }
                }

                if (qtyToSet > 0)
                {
                    await WriteAsync("stock.move.line", mlId, new JsonObject
                    {
                        ["qty_done"] = qtyToSet,
                        ["picked"] = true
                    });
                }
            }
        }
        _logger.LogInformation("Set qty_done on move lines for picking id={PickingId}", pickingId);

        // 5. button_validate() — allow backorder for partial deliveries
        bool isPartial = deliveredItems != null && deliveredItems.Count > 0;
        var validateContext = new JsonObject
        {
            ["skip_immediate"] = true
        };
        if (!isPartial)
        {
            // Full delivery — skip backorder wizard
            validateContext["skip_backorder"] = true;
        }
        // Partial delivery: do NOT set skip_backorder so Odoo creates
        // a backorder for the remaining items

        await ExecuteMethodWithContextAsync("stock.picking", "button_validate",
            new JsonArray { pickingId }, validateContext);
        _logger.LogInformation("Validated picking id={PickingId} (partial={IsPartial})", pickingId, isPartial);

        // 6. Composite check + Write SAP delivery DocEntry, date, and pick list ID
        var existingPickingData = await ReadAsync("stock.picking", pickingId, new JsonArray
        {
            JsonValue.Create("x_sap_delivery_docentry"),
            JsonValue.Create("scheduled_date"),
            JsonValue.Create("name")
        });

        int existingDeliveryDocEntry = existingPickingData?["x_sap_delivery_docentry"]?.GetValue<int>() ?? 0;
        if (existingDeliveryDocEntry > 0)
        {
            int incomingDocEntry = 0;
            int.TryParse(request.SapDeliveryNo, out incomingDocEntry);
            string pickingName = existingPickingData?["name"]?.GetValue<string>() ?? "";

            if (existingDeliveryDocEntry != incomingDocEntry)
            {
                _logger.LogError(
                    "Write-back MISMATCH for picking id={PickingId} ('{PickingName}'): " +
                    "existing x_sap_delivery_docentry={ExistingId} vs incoming={IncomingId}, " +
                    "SO ref={SoRef} — skipping delivery write-back",
                    pickingId, pickingName, existingDeliveryDocEntry, incomingDocEntry, soId);
                // Still return the response since the picking was already validated above
                var skipData = await ReadAsync("stock.picking", pickingId, new JsonArray
                {
                    JsonValue.Create("name"), JsonValue.Create("state")
                });
                return new DeliveryUpdateResponse
                {
                    UOdooSoId = soId, PickingId = pickingId,
                    PickingName = skipData?["name"]?.GetValue<string>() ?? "",
                    State = skipData?["state"]?.GetValue<string>() ?? "",
                    SapDeliveryNo = request.SapDeliveryNo
                };
            }
            _logger.LogInformation(
                "Write-back match confirmed for picking id={PickingId} ('{PickingName}'): " +
                "SAP DocEntry={SapDocEntry}, SO ref={SoRef} — proceeding with re-sync",
                pickingId, pickingName, existingDeliveryDocEntry, soId);
        }

        var writeValues = new JsonObject();

        if (int.TryParse(request.SapDeliveryNo, out int deliveryDocEntry))
            writeValues["x_sap_delivery_docentry"] = deliveryDocEntry;
        else
            writeValues["x_sap_delivery_docentry"] = request.SapDeliveryNo;

        if (request.DeliveryDate.HasValue)
            writeValues["x_sap_delivery_date"] = request.DeliveryDate.Value.ToString("yyyy-MM-dd HH:mm:ss");

        var soData = await ReadAsync("sale.order", soDbId, new JsonArray
        {
            JsonValue.Create("x_sap_picklist_id")
        });
        var pickListId = soData?["x_sap_picklist_id"]?.GetValue<int>() ?? 0;
        if (pickListId > 0)
            writeValues["x_sap_picklist_id"] = pickListId;

        await WriteAsync("stock.picking", pickingId, writeValues);
        _logger.LogInformation("Wrote SAP delivery DocEntry={DocEntry} onto picking id={PickingId}",
            request.SapDeliveryNo, pickingId);

        // 7. Read back picking state and name
        var pickingData = await ReadAsync("stock.picking", pickingId, new JsonArray
        {
            JsonValue.Create("name"), JsonValue.Create("state")
        });

        string pickingName = pickingData?["name"]?.GetValue<string>() ?? "";
        string state = pickingData?["state"]?.GetValue<string>() ?? "";

        return new DeliveryUpdateResponse
        {
            UOdooSoId = soId,
            PickingId = pickingId,
            PickingName = pickingName,
            State = state,
            SapDeliveryNo = request.SapDeliveryNo
        };
    }

    public async Task<InvoiceWriteBackResponse> UpdateInvoiceSapFieldsAsync(InvoiceWriteBackRequest request)
    {
        if (!_settings.UseBearerAuth)
            await EnsureAuthenticatedAsync();

        int invoiceId = request.OdooInvoiceId;

        _logger.LogInformation(
            "Writing SAP invoice fields back to Odoo — OdooInvoiceId={OdooInvoiceId}, SapDocEntry={SapDocEntry}, LineCount={LineCount}",
            invoiceId, request.SapDocEntry, request.Lines.Count);

        // 0. Composite check: read existing SAP fields + invoice_date before writing
        var existingData = await ReadAsync("account.move", invoiceId, new JsonArray
        {
            JsonValue.Create("x_sap_invoice_docentry"),
            JsonValue.Create("invoice_date"),
            JsonValue.Create("name")
        });

        int existingSapId = existingData?["x_sap_invoice_docentry"]?.GetValue<int>() ?? 0;
        if (existingSapId > 0)
        {
            string odooDate = existingData?["invoice_date"]?.GetValue<string>() ?? "";
            string invoiceName = existingData?["name"]?.GetValue<string>() ?? "";
            // Compare date-only (first 10 chars) with the SAP posting date from the request
            // If SAP DocEntry matches and dates align, allow re-sync; otherwise skip
            if (existingSapId != request.SapDocEntry)
            {
                _logger.LogError(
                    "Write-back MISMATCH for invoice id={InvoiceId} ('{InvoiceName}'): " +
                    "existing x_sap_invoice_docentry={ExistingId} vs incoming={IncomingId} — skipping",
                    invoiceId, invoiceName, existingSapId, request.SapDocEntry);
                return new InvoiceWriteBackResponse
                {
                    OdooInvoiceId = invoiceId, SapDocEntry = request.SapDocEntry,
                    LinesUpdated = 0, Success = false
                };
            }
            _logger.LogInformation(
                "Write-back match confirmed for invoice id={InvoiceId} ('{InvoiceName}'): " +
                "SAP DocEntry={SapDocEntry}, date={OdooDate} — proceeding with re-sync",
                invoiceId, invoiceName, existingSapId, odooDate);
        }

        // 1. Write x_sap_invoice_docentry (and base document refs) on the account.move header
        var headerValues = new JsonObject
        {
            ["x_sap_invoice_docentry"] = request.SapDocEntry
        };
        if (request.SapDeliveryDocEntry.HasValue && request.SapDeliveryDocEntry.Value > 0)
            headerValues["x_sap_delivery_docentry"] = request.SapDeliveryDocEntry.Value;
        if (request.SapSalesOrderDocEntry.HasValue && request.SapSalesOrderDocEntry.Value > 0)
            headerValues["x_sap_docentry"] = request.SapSalesOrderDocEntry.Value;

        await WriteAsync("account.move", invoiceId, headerValues);

        _logger.LogInformation(
            "Wrote x_sap_invoice_docentry={SapDocEntry}, x_sap_delivery_docentry={DeliveryDocEntry}, " +
            "x_sap_docentry={SoDocEntry} on account.move id={InvoiceId}",
            request.SapDocEntry, request.SapDeliveryDocEntry, request.SapSalesOrderDocEntry, invoiceId);

        // 2. Read the invoice line IDs (account.move.line) for this invoice,
        //    filtering to product lines only (exclude tax/rounding lines).
        var lineIds = await SearchAsync("account.move.line", new JsonArray
        {
            new JsonArray { JsonValue.Create("move_id"), JsonValue.Create("="), JsonValue.Create(invoiceId) },
            new JsonArray { JsonValue.Create("display_type"), JsonValue.Create("="), JsonValue.Create("product") }
        });

        _logger.LogInformation(
            "Found {Count} product line(s) on Odoo invoice id={InvoiceId}",
            lineIds.Count, invoiceId);

        // 3. Write x_sap_invoice_linenum on each line by position.
        //    Odoo lines are matched to SAP lines by order (first → 0, second → 1, …).
        //    NOTE: GrossBuyPrice is NOT stored on invoice lines — it flows through
        //    the COGS journal entry (CreateOrUpdateCogsJournalAsync) instead.
        int linesUpdated = 0;

        for (int i = 0; i < Math.Min(lineIds.Count, request.Lines.Count); i++)
        {
            int odooLineId = lineIds[i];
            var sapLine = request.Lines[i];

            await WriteAsync("account.move.line", odooLineId, new JsonObject
            {
                ["x_sap_invoice_linenum"] = sapLine.SapLineNum
            });

            _logger.LogDebug(
                "Odoo line id={OdooLineId}: x_sap_invoice_linenum={LineNum}",
                odooLineId, sapLine.SapLineNum);

            linesUpdated++;
        }

        if (lineIds.Count != request.Lines.Count)
        {
            _logger.LogWarning(
                "Line count mismatch: Odoo has {OdooCount} product lines, SAP returned {SapCount} lines. " +
                "Updated {Updated} lines by position.",
                lineIds.Count, request.Lines.Count, linesUpdated);
        }

        _logger.LogInformation(
            "Invoice write-back complete — OdooInvoiceId={OdooInvoiceId}, LinesUpdated={LinesUpdated}",
            invoiceId, linesUpdated);

        return new InvoiceWriteBackResponse
        {
            OdooInvoiceId = invoiceId,
            SapDocEntry = request.SapDocEntry,
            LinesUpdated = linesUpdated,
            Success = true
        };
    }

    // ── COGS Journal Entry automation (Step 4) ─────────────────────

    public async Task UpdateIncomingPaymentAsync(IncomingPaymentWriteBackRequest request)
    {
        if (!_settings.UseBearerAuth)
            await EnsureAuthenticatedAsync();

        int paymentId = request.OdooPaymentId;

        _logger.LogInformation(
            "Writing SAP Incoming Payment fields back to Odoo — OdooPaymentId={OdooPaymentId}, " +
            "SapDocEntry={SapDocEntry}, SapDocNum={SapDocNum}",
            paymentId, request.SapDocEntry, request.SapDocNum);

        // Composite check: read existing SAP fields + date before writing
        var existingData = await ReadAsync("account.payment", paymentId, new JsonArray
        {
            JsonValue.Create("x_sap_inpay_docentry"),
            JsonValue.Create("date"),
            JsonValue.Create("name")
        });

        int existingSapId = existingData?["x_sap_inpay_docentry"]?.GetValue<int>() ?? 0;
        if (existingSapId > 0 && existingSapId != request.SapDocEntry)
        {
            string paymentName = existingData?["name"]?.GetValue<string>() ?? "";
            _logger.LogError(
                "Write-back MISMATCH for payment id={PaymentId} ('{PaymentName}'): " +
                "existing x_sap_inpay_docentry={ExistingId} vs incoming={IncomingId} — skipping",
                paymentId, paymentName, existingSapId, request.SapDocEntry);
            return;
        }

        if (existingSapId > 0)
        {
            string paymentName = existingData?["name"]?.GetValue<string>() ?? "";
            _logger.LogInformation(
                "Write-back match confirmed for payment id={PaymentId} ('{PaymentName}'): " +
                "SAP DocEntry={SapDocEntry} — proceeding with re-sync",
                paymentId, paymentName, existingSapId);
        }

        await WriteAsync("account.payment", paymentId, new JsonObject
        {
            ["x_sap_inpay_docentry"] = request.SapDocEntry,
            ["x_sap_inpay_docnum"] = request.SapDocNum
        });

        _logger.LogInformation(
            "Incoming Payment write-back complete — OdooPaymentId={OdooPaymentId}, " +
            "x_sap_inpay_docentry={SapDocEntry}, x_sap_inpay_docnum={SapDocNum}",
            paymentId, request.SapDocEntry, request.SapDocNum);
    }

    // ── Credit Memo write-back ──────────────────────────────────────

    public async Task UpdateCreditMemoAsync(CreditMemoWriteBackRequest request)
    {
        if (!_settings.UseBearerAuth)
            await EnsureAuthenticatedAsync();

        int invoiceId = request.OdooInvoiceId;

        _logger.LogInformation(
            "Writing SAP Credit Memo fields back to Odoo — OdooInvoiceId={OdooInvoiceId}, " +
            "SapDocEntry={SapDocEntry}",
            invoiceId, request.SapDocEntry);

        var creditValues = new JsonObject
        {
            ["x_sap_credit_docentry"] = request.SapDocEntry
        };
        if (request.SapSalesOrderDocEntry.HasValue && request.SapSalesOrderDocEntry.Value > 0)
            creditValues["x_sap_docentry"] = request.SapSalesOrderDocEntry.Value;
        if (request.SapDeliveryDocEntry.HasValue && request.SapDeliveryDocEntry.Value > 0)
            creditValues["x_sap_delivery_docentry"] = request.SapDeliveryDocEntry.Value;
        if (request.SapBaseInvoiceDocEntry.HasValue && request.SapBaseInvoiceDocEntry.Value > 0)
            creditValues["x_sap_invoice_docentry"] = request.SapBaseInvoiceDocEntry.Value;

        await WriteAsync("account.move", invoiceId, creditValues);

        _logger.LogInformation(
            "Credit Memo write-back complete — OdooInvoiceId={OdooInvoiceId}, " +
            "x_sap_credit_docentry={SapDocEntry}, x_sap_docentry={SoDocEntry}, " +
            "x_sap_delivery_docentry={DelDocEntry}, x_sap_invoice_docentry={InvDocEntry}",
            invoiceId, request.SapDocEntry, request.SapSalesOrderDocEntry,
            request.SapDeliveryDocEntry, request.SapBaseInvoiceDocEntry);
    }

    // ── Goods Return write-back ─────────────────────────────────────

    public async Task UpdateGoodsReturnAsync(GoodsReturnWriteBackRequest request)
    {
        if (!_settings.UseBearerAuth)
            await EnsureAuthenticatedAsync();

        int pickingId = request.OdooPickingId;

        _logger.LogInformation(
            "Writing SAP Goods Return fields back to Odoo — OdooPickingId={OdooPickingId}, " +
            "SapDocEntry={SapDocEntry}",
            pickingId, request.SapDocEntry);

        await WriteAsync("stock.picking", pickingId, new JsonObject
        {
            ["x_sap_return_delivery_docentry"] = request.SapDocEntry
        });

        _logger.LogInformation(
            "Goods Return write-back complete — OdooPickingId={OdooPickingId}, " +
            "x_sap_return_delivery_docentry={SapDocEntry}",
            pickingId, request.SapDocEntry);
    }

    // ── COGS Journal Entry automation (Step 4) ─────────────────────

    public async Task<CogsJournalResponse> CreateOrUpdateCogsJournalAsync(CogsJournalRequest request)
    {
        if (!_settings.UseBearerAuth)
            await EnsureAuthenticatedAsync();

        // Resolve invoice ID — use OdooInvoiceId directly when available,
        // otherwise search by x_sap_invoice_docentry.
        int invoiceId;

        if (request.OdooInvoiceId.HasValue && request.OdooInvoiceId.Value > 0)
        {
            invoiceId = request.OdooInvoiceId.Value;
            _logger.LogInformation(
                "COGS: using OdooInvoiceId={InvoiceId} for DocEntry={DocEntry}",
                invoiceId, request.DocEntry);
        }
        else
        {
            var invoiceRecords = await SearchAsync("account.move", new JsonArray
            {
                new JsonArray { JsonValue.Create("move_type"), JsonValue.Create("in"),
                    new JsonArray { JsonValue.Create("out_invoice"), JsonValue.Create("out_refund") } },
                new JsonArray { JsonValue.Create("x_sap_invoice_docentry"), JsonValue.Create("="),
                    JsonValue.Create(request.DocEntry) }
            });

            if (invoiceRecords.Count == 0)
                throw new InvalidOperationException(
                    $"Odoo invoice not found for SAP DocEntry={request.DocEntry}. " +
                    "Ensure x_sap_invoice_docentry has been written back to Odoo.");

            invoiceId = invoiceRecords[0];
            _logger.LogInformation(
                "COGS: found invoice id={InvoiceId} for DocEntry={DocEntry}",
                invoiceId, request.DocEntry);
        }

        // Build cost lines — skip zero-qty / zero-cost
        var sapLines = new JsonArray();
        foreach (var line in request.Lines)
        {
            if (line.Quantity == 0) continue;
            double unitCost = line.UnitCost ?? 0;
            if (line.StockSum.HasValue)
                unitCost = line.StockSum.Value / line.Quantity;
            if (unitCost == 0) continue;

            sapLines.Add(new JsonObject
            {
                ["item_code"] = line.ItemCode,
                ["quantity"] = line.Quantity,
                ["unit_cost"] = unitCost,
                ["line_num"] = line.LineNum,
            });
        }

        if (sapLines.Count == 0)
        {
            _logger.LogWarning("No COGS lines with cost data for DocEntry={DocEntry}. Skipping.", request.DocEntry);
            return new CogsJournalResponse
            {
                SapDocEntry = request.DocEntry,
                OdooInvoiceId = invoiceId,
                Action = "skipped",
                TotalCogs = 0
            };
        }

        string hash = ComputeCogsHash(request);

        // Call Odoo's native create_cogs_journal method — Odoo handles
        // all JE creation, balancing, currency, and posting internally.
        var rpcVals = new JsonObject
        {
            ["invoice_id"] = invoiceId,
            ["doc_entry"] = request.DocEntry,
            ["journal_id"] = _settings.CogsJournalId,
            ["cogs_account_id"] = _settings.CogsAccountId,
            ["clearing_account_id"] = _settings.CogsClearingAccountId,
            ["cogs_hash"] = hash,
            ["lines"] = sapLines,
        };

        _logger.LogInformation(
            "Calling Odoo create_cogs_journal for invoice={InvoiceId}, DocEntry={DocEntry}, Lines={LineCount}",
            invoiceId, request.DocEntry, sapLines.Count);

        JsonNode? result;
        if (_settings.UseBearerAuth)
        {
            result = await SendJson2Async("account.move", "create_cogs_journal",
                new JsonObject { ["vals"] = rpcVals });
        }
        else
        {
            result = await CallObjectMethodAsync("account.move", "create_cogs_journal",
                new JsonArray { rpcVals });
        }

        var resultObj = result?.AsObject();
        string action = resultObj?["action"]?.GetValue<string>() ?? "unknown";
        int cogsJeId = resultObj?["cogs_journal_entry_id"]?.GetValue<int>() ?? 0;
        double totalCogs = resultObj?["total_cogs"]?.GetValue<double>() ?? 0;

        _logger.LogInformation(
            "COGS {Action}: JeId={JeId}, TotalCogs={TotalCogs}, DocEntry={DocEntry}",
            action, cogsJeId, totalCogs, request.DocEntry);

        return new CogsJournalResponse
        {
            SapDocEntry = request.DocEntry,
            OdooInvoiceId = invoiceId,
            CogsJournalEntryId = cogsJeId,
            Action = action,
            Hash = hash,
            TotalCogs = totalCogs
        };
    }

    // ── COGS line matching (4.3) ─────────────────────────────────────

    private List<(CogsJournalLineRequest SapLine, JsonObject? OdooLine)> MatchLinesToOdoo(
        List<CogsJournalLineRequest> sapLines, List<JsonObject> odooLines)
    {
        var result = new List<(CogsJournalLineRequest, JsonObject?)>();
        var usedOdooIndices = new HashSet<int>();

        foreach (var sapLine in sapLines)
        {
            JsonObject? matched = null;

            // Best match: by x_sap_invoice_linenum (when LineNum is available)
            if (sapLine.LineNum.HasValue)
            {
                for (int i = 0; i < odooLines.Count; i++)
                {
                    if (usedOdooIndices.Contains(i)) continue;
                    var lineNum = odooLines[i]["x_sap_invoice_linenum"];
                    if (lineNum != null && lineNum.GetValue<int>() == sapLine.LineNum.Value)
                    {
                        matched = odooLines[i];
                        usedOdooIndices.Add(i);
                        break;
                    }
                }
            }

            // Fallback: match by ItemCode + Quantity
            if (matched == null)
            {
                for (int i = 0; i < odooLines.Count; i++)
                {
                    if (usedOdooIndices.Contains(i)) continue;

                    // Check item code via x_sap_item_code field
                    string odooItemCode = odooLines[i]["x_sap_item_code"]?.GetValue<string>() ?? "";
                    double odooQty = odooLines[i]["quantity"]?.GetValue<double>() ?? 0;

                    if (string.Equals(odooItemCode, sapLine.ItemCode, StringComparison.OrdinalIgnoreCase)
                        && Math.Abs(odooQty - sapLine.Quantity) < 0.001)
                    {
                        matched = odooLines[i];
                        usedOdooIndices.Add(i);
                        break;
                    }
                }
            }

            // Last resort: first unused line (consume by sequence order)
            if (matched == null)
            {
                for (int i = 0; i < odooLines.Count; i++)
                {
                    if (usedOdooIndices.Contains(i)) continue;
                    matched = odooLines[i];
                    usedOdooIndices.Add(i);
                    _logger.LogWarning(
                        "COGS fallback: matched SAP line ItemCode={ItemCode} to Odoo line id={OdooLineId} by sequence order",
                        sapLine.ItemCode, odooLines[i]["id"]?.GetValue<int>());
                    break;
                }
            }

            if (matched == null)
            {
                _logger.LogWarning(
                    "No Odoo line match for SAP line ItemCode={ItemCode}, Qty={Qty}. " +
                    "COGS debit will have no analytic distribution.",
                    sapLine.ItemCode, sapLine.Quantity);
            }

            result.Add((sapLine, matched));
        }

        return result;
    }

    /// <summary>
    /// Reads the decimal_places for a currency from Odoo.
    /// Falls back to 2 if the read fails.
    /// </summary>
    private async Task<int> GetCurrencyDecimalsAsync(int currencyId)
    {
        try
        {
            var curr = await ReadAsync("res.currency", currencyId, new JsonArray
            {
                JsonValue.Create("decimal_places")
            });
            if (curr != null && curr["decimal_places"] != null)
                return curr["decimal_places"]!.GetValue<int>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Could not read decimal_places for currency id={CurrencyId}: {Error}. Defaulting to 2.",
                currencyId, ex.Message);
        }
        return 2;
    }

    /// <summary>
    /// Returns true if the analytic_distribution JSON is a non-null, non-false value.
    /// Odoo returns false (as a JSON boolean/string) when no analytics are set.
    /// </summary>
    private static bool IsValidAnalyticDistribution(JsonNode? analyticDist)
    {
        if (analyticDist == null) return false;
        if (analyticDist is JsonValue jv)
        {
            var raw = jv.ToString();
            return raw != "false" && raw != "False" && raw != "";
        }
        // JsonObject with actual distribution data
        return true;
    }

    // ── COGS hash generation (4.7) ───────────────────────────────────

    internal static string ComputeCogsHash(CogsJournalRequest request)
    {
        // Sort lines by LineNum if available, then by ItemCode for stability
        var sortedLines = request.Lines
            .OrderBy(l => l.LineNum ?? int.MaxValue)
            .ThenBy(l => l.ItemCode)
            .ToList();

        var sb = new StringBuilder();
        sb.Append(request.DocEntry);

        foreach (var line in sortedLines)
        {
            double lineCogs = line.StockSum ?? (line.UnitCost ?? 0) * line.Quantity;
            sb.Append('|');
            sb.Append(line.ItemCode);
            sb.Append(':');
            sb.Append(line.Quantity.ToString("F4"));
            sb.Append(':');
            sb.Append(lineCogs.ToString("F4"));
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ── JE payload builders (4.6) ────────────────────────────────────

    private JsonObject BuildJeCreatePayload(
        string invoiceName, int docEntry, string date, string hash, int invoiceId,
        List<(double LineCogs, string ItemCode, int? SapLineNum, JsonNode? AnalyticDistribution, string ProductName)> cogsLines,
        double totalCogs, int currencyDecimals = 2, int currencyId = 0)
    {
        var lineCommands = new JsonArray();
        double debitSum = 0;

        foreach (var (lineCogs, itemCode, sapLineNum, analyticDist, productName) in cogsLines)
        {
            var rounded = Math.Round(lineCogs, currencyDecimals);
            debitSum += rounded;
            var lineNumLabel = sapLineNum.HasValue ? sapLineNum.Value.ToString() : "?";
            var debitLine = new JsonObject
            {
                ["account_id"] = _settings.CogsAccountId,
                ["balance"] = rounded,
                ["amount_currency"] = rounded,
                ["name"] = $"COGS | {productName} | INV {invoiceName} | SAP line {lineNumLabel}"
            };
            if (currencyId > 0)
                debitLine["currency_id"] = currencyId;

            if (IsValidAnalyticDistribution(analyticDist))
            {
                debitLine["analytic_distribution"] = analyticDist!.DeepClone();
            }

            lineCommands.Add(new JsonArray { 0, 0, debitLine });
        }

        var creditTotal = Math.Round(debitSum, currencyDecimals);
        var creditLine = new JsonObject
        {
            ["account_id"] = _settings.CogsClearingAccountId,
            ["balance"] = -creditTotal,
            ["amount_currency"] = -creditTotal,
            ["name"] = $"COGS Clearing | INV {invoiceName}"
        };
        if (currencyId > 0)
            creditLine["currency_id"] = currencyId;
        lineCommands.Add(new JsonArray { 0, 0, creditLine });

        return new JsonObject
        {
            ["move_type"] = "entry",
            ["journal_id"] = _settings.CogsJournalId,
            ["date"] = date,
            ["ref"] = $"COGS | {invoiceName} | SAP DocEntry {docEntry}",
            ["x_cogs_for_invoice_id"] = invoiceId,
            ["x_cogs_import_hash"] = hash,
            ["line_ids"] = lineCommands
        };
    }

    private JsonObject BuildJeWritePayload(
        string invoiceName, int docEntry, string date, string hash, int invoiceId,
        List<(double LineCogs, string ItemCode, int? SapLineNum, JsonNode? AnalyticDistribution, string ProductName)> cogsLines,
        List<int> existingLineIds, int currencyDecimals = 2, int currencyId = 0)
    {
        var lineCommands = new JsonArray();

        foreach (int lineId in existingLineIds)
        {
            lineCommands.Add(new JsonArray { 2, lineId, 0 });
        }

        double debitSum = 0;
        foreach (var (lineCogs, itemCode, sapLineNum, analyticDist, productName) in cogsLines)
        {
            var rounded = Math.Round(lineCogs, currencyDecimals);
            debitSum += rounded;
            var lineNumLabel = sapLineNum.HasValue ? sapLineNum.Value.ToString() : "?";
            var debitLine = new JsonObject
            {
                ["account_id"] = _settings.CogsAccountId,
                ["balance"] = rounded,
                ["amount_currency"] = rounded,
                ["name"] = $"COGS | {productName} | INV {invoiceName} | SAP line {lineNumLabel}"
            };
            if (currencyId > 0)
                debitLine["currency_id"] = currencyId;

            if (IsValidAnalyticDistribution(analyticDist))
            {
                debitLine["analytic_distribution"] = analyticDist!.DeepClone();
            }

            lineCommands.Add(new JsonArray { 0, 0, debitLine });
        }

        var creditTotal = Math.Round(debitSum, currencyDecimals);
        var creditLine = new JsonObject
        {
            ["account_id"] = _settings.CogsClearingAccountId,
            ["balance"] = -creditTotal,
            ["amount_currency"] = -creditTotal,
            ["name"] = $"COGS Clearing | INV {invoiceName}"
        };
        if (currencyId > 0)
            creditLine["currency_id"] = currencyId;
        lineCommands.Add(new JsonArray { 0, 0, creditLine });

        return new JsonObject
        {
            ["date"] = date,
            ["ref"] = $"COGS | {invoiceName} | SAP DocEntry {docEntry}",
            ["x_cogs_import_hash"] = hash,
            ["line_ids"] = lineCommands
        };
    }

    // ── Odoo JSON-RPC helpers ────────────────────────────────────────

    public async Task<OdooPingResponse> PingAsync()
    {
        if (_settings.UseBearerAuth)
        {
            // Test connectivity with a safe no-op call using the /json/2/ API
            await SendJson2Async("res.partner", "search_read", new JsonObject
            {
                ["domain"] = new JsonArray
                {
                    new JsonArray { JsonValue.Create("id"), JsonValue.Create("="), JsonValue.Create(0) }
                },
                ["fields"] = new JsonArray { JsonValue.Create("id") },
                ["limit"] = 1
            });

            return new OdooPingResponse
            {
                Connected = true,
                Uid = 0,
                Database = _settings.Database,
                ServerVersion = null,
                BaseUrl = _settings.BaseUrl,
                UserName = _settings.UserName
            };
        }

        // Classic session auth mode
        _uid = null;
        await EnsureAuthenticatedAsync();

        // Optionally get server version via /web/session/get_session_info
        string? serverVersion = null;
        try
        {
            var versionResult = await CallJsonRpcAsync("/web/session/get_session_info", new JsonObject());
            serverVersion = versionResult?["server_version"]?.GetValue<string>();
        }
        catch
        {
            // Version info is optional — don't fail the ping
        }

        return new OdooPingResponse
        {
            Connected = _uid.HasValue,
            Uid = _uid ?? 0,
            Database = _settings.Database,
            ServerVersion = serverVersion,
            BaseUrl = _settings.BaseUrl,
            UserName = _settings.UserName
        };
    }

    private async Task EnsureAuthenticatedAsync()
    {
        if (_uid.HasValue) return;

        var result = await CallJsonRpcAsync("/web/session/authenticate", new JsonObject
        {
            ["db"] = _settings.Database,
            ["login"] = _settings.UserName,
            ["password"] = _settings.Password
        });

        _uid = result?["uid"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Odoo authentication failed — uid is null.");

        _logger.LogInformation("Authenticated with Odoo as uid={Uid}", _uid);
    }

    private async Task<List<int>> SearchAsync(string model, JsonArray domain)
    {
        if (_settings.UseBearerAuth)
        {
            var result = await SendJson2Async(model, "search", new JsonObject { ["domain"] = domain });
            return result?.AsArray().Select(n => n!.GetValue<int>()).ToList() ?? [];
        }

        var classicResult = await CallObjectMethodAsync(model, "search", new JsonArray { domain });
        return classicResult?.AsArray().Select(n => n!.GetValue<int>()).ToList() ?? [];
    }

    private async Task ExecuteMethodAsync(string model, string method, JsonArray ids)
    {
        if (_settings.UseBearerAuth)
        {
            await SendJson2Async(model, method, new JsonObject { ["ids"] = ids });
            return;
        }

        await CallObjectMethodAsync(model, method, new JsonArray { ids });
    }

    private async Task ExecuteMethodWithContextAsync(string model, string method, JsonArray ids, JsonObject context)
    {
        if (_settings.UseBearerAuth)
        {
            await SendJson2Async(model, method, new JsonObject { ["ids"] = ids, ["context"] = context });
            return;
        }

        var kwargs = new JsonObject { ["context"] = context };
        await CallObjectMethodAsync(model, method, new JsonArray { ids }, kwargs);
    }

    private async Task WriteAsync(string model, int id, JsonObject values)
    {
        if (_settings.UseBearerAuth)
        {
            await SendJson2Async(model, "write", new JsonObject { ["ids"] = new JsonArray { id }, ["vals"] = values });
            return;
        }

        // Classic JSON-RPC: args = [[id], vals]
        await CallObjectMethodAsync(model, "write", new JsonArray
        {
            new JsonArray { id },
            values
        });
    }

    private async Task<JsonObject?> ReadAsync(string model, int id, JsonArray fields)
    {
        if (_settings.UseBearerAuth)
        {
            var result = await SendJson2Async(model, "read", new JsonObject { ["ids"] = new JsonArray { id }, ["fields"] = fields });
            return result?.AsArray().FirstOrDefault()?.AsObject();
        }

        var classicResult = await CallObjectMethodAsync(model, "read", new JsonArray
        {
            new JsonArray { id },
            fields
        });

        return classicResult?.AsArray().FirstOrDefault()?.AsObject();
    }

    private async Task<List<JsonObject>> SearchReadAsync(string model, JsonArray domain, JsonArray fields)
    {
        if (_settings.UseBearerAuth)
        {
            var result = await SendJson2Async(model, "search_read", new JsonObject
            {
                ["domain"] = domain,
                ["fields"] = fields
            });
            return result?.AsArray().Select(n => n!.AsObject()).ToList() ?? [];
        }

        var classicResult = await CallObjectMethodAsync(model, "search_read", new JsonArray
        {
            domain,
            fields
        });
        return classicResult?.AsArray().Select(n => n!.AsObject()).ToList() ?? [];
    }

    private async Task<int> CreateAsync(string model, JsonObject values)
    {
        if (_settings.UseBearerAuth)
        {
            // JSON2 API binds kwargs to method signature: create(self, vals_list)
            // vals_list must be a JSON array of record dicts.
            var result = await SendJson2Async(model, "create", new JsonObject
            {
                ["vals_list"] = new JsonArray { values }
            });

            // create() may return a single ID or an array of IDs via JSON2
            if (result is JsonArray arr && arr.Count > 0)
                return arr[0]!.GetValue<int>();
            return result?.GetValue<int>()
                ?? throw new InvalidOperationException($"Odoo create on {model} returned null.");
        }

        var classicResult = await CallObjectMethodAsync(model, "create", new JsonArray { values });
        return classicResult?.GetValue<int>() ?? throw new InvalidOperationException($"Odoo create on {model} returned null.");
    }

    private async Task<JsonNode?> CallObjectMethodAsync(
        string model, string method, JsonArray args, JsonObject? kwargs = null)
    {
        var callArgs = new JsonArray
        {
            JsonValue.Create(_settings.Database),
            JsonValue.Create(_uid!.Value),
            JsonValue.Create(_settings.Password),
            JsonValue.Create(model),
            JsonValue.Create(method)
        };

        // Flatten args into callArgs
        foreach (var arg in args)
        {
            callArgs.Add(arg?.DeepClone());
        }

        if (kwargs != null)
            callArgs.Add(kwargs);

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "call",
            ["id"] = Interlocked.Increment(ref _rpcId),
            ["params"] = new JsonObject
            {
                ["service"] = "object",
                ["method"] = "execute_kw",
                ["args"] = callArgs
            }
        };

        return await SendRpcAsync("/jsonrpc", payload);
    }

    private async Task<JsonNode?> CallJsonRpcAsync(string path, JsonObject parameters)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "call",
            ["id"] = Interlocked.Increment(ref _rpcId),
            ["params"] = parameters
        };

        return await SendRpcAsync(path, payload);
    }

    private async Task<JsonNode?> SendJson2Async(string model, string method, JsonObject body)
    {
        var url = _settings.BaseUrl.TrimEnd('/') + $"/json/2/{model}/{method}";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Authorization", $"Bearer {_settings.EffectiveApiKey}");
        request.Headers.Add("X-Odoo-Database", _settings.Database);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        _logger.LogDebug("Odoo /json/2/ request: {Method} {Url} Body: {Body}", "POST", url, body.ToJsonString());

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Odoo /json/2/ error: {StatusCode} {Url} Response: {Response}",
                (int)response.StatusCode, url, responseBody);

            string errorMessage;
            try
            {
                var errorJson = JsonNode.Parse(responseBody);
                errorMessage = errorJson?["error"]?["data"]?["message"]?.GetValue<string>()
                    ?? errorJson?["error"]?["message"]?.GetValue<string>()
                    ?? $"HTTP {(int)response.StatusCode}";

                // Include traceback excerpt for debugging
                var tb = errorJson?["error"]?["data"]?["debug"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(tb))
                {
                    // Last 3 lines of the traceback
                    var tbLines = tb.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var tail = string.Join(" | ", tbLines.TakeLast(3));
                    errorMessage += $" [{tail}]";
                }
            }
            catch (JsonException)
            {
                errorMessage = $"HTTP {(int)response.StatusCode}: {responseBody}";
            }
            throw new InvalidOperationException($"Odoo API error: {errorMessage}");
        }

        return JsonNode.Parse(responseBody);
    }

    private async Task<JsonNode?> SendRpcAsync(string path, JsonObject payload)
    {
        var url = _settings.BaseUrl.TrimEnd('/') + path;
        var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body);

        var error = json?["error"];
        if (error != null)
        {
            var errorMessage = error["data"]?["message"]?.GetValue<string>()
                ?? error["message"]?.GetValue<string>()
                ?? "Unknown Odoo RPC error";
            throw new InvalidOperationException($"Odoo RPC error: {errorMessage}");
        }

        return json?["result"];
    }
}
