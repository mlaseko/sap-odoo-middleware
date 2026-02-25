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

        // 1. Find sale.order by name (which matches the Odoo SO identifier, e.g. "SO0042")
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

        // 4. Set qty_done = quantity (demand) on each move line
        //    In Odoo 18, action_set_quantities_to_reservation does not exist.
        //    Instead, we read each stock.move.line, get its 'quantity' (demand),
        //    and write it to 'qty_done', plus set 'picked' = true.
        var moveLineIds = await SearchAsync("stock.move.line", new JsonArray
        {
            new JsonArray { JsonValue.Create("picking_id"), JsonValue.Create("="), JsonValue.Create(pickingId) }
        });

        if (moveLineIds.Count > 0)
        {
            foreach (var mlId in moveLineIds)
            {
                // Read the demand quantity
                var mlData = await ReadAsync("stock.move.line", mlId, new JsonArray
                {
                    JsonValue.Create("quantity")
                });

                var demandQty = mlData?["quantity"]?.GetValue<double>() ?? 0;

                // Write qty_done = demand quantity, picked = true
                await WriteAsync("stock.move.line", mlId, new JsonObject
                {
                    ["qty_done"] = demandQty,
                    ["picked"] = true
                });
            }
        }
        _logger.LogInformation("Set qty_done on {Count} move lines for picking id={PickingId}", moveLineIds.Count, pickingId);

        // 5. button_validate() — with context to skip backorder/immediate-transfer wizards
        await ExecuteMethodWithContextAsync("stock.picking", "button_validate", new JsonArray { pickingId },
            new JsonObject
            {
                ["skip_backorder"] = true,
                ["skip_immediate"] = true
            });
        _logger.LogInformation("Validated picking id={PickingId}", pickingId);

        // 6. Write SAP delivery DocEntry and date onto the picking
        var writeValues = new JsonObject();

        // x_sap_delivery_docentry is an integer field in Odoo
        if (int.TryParse(request.SapDeliveryNo, out int deliveryDocEntry))
            writeValues["x_sap_delivery_docentry"] = deliveryDocEntry;
        else
            writeValues["x_sap_delivery_docentry"] = request.SapDeliveryNo;

        // x_sap_delivery_date is a datetime field in Odoo
        if (request.DeliveryDate.HasValue)
            writeValues["x_sap_delivery_date"] = request.DeliveryDate.Value.ToString("yyyy-MM-dd HH:mm:ss");

        await WriteAsync("stock.picking", pickingId, writeValues);
        _logger.LogInformation("Wrote SAP delivery DocEntry={DocEntry} onto picking id={PickingId}", request.SapDeliveryNo, pickingId);

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

        // 1. Write x_sap_invoice_docentry on the account.move header
        await WriteAsync("account.move", invoiceId, new JsonObject
        {
            ["x_sap_invoice_docentry"] = request.SapDocEntry
        });

        _logger.LogInformation(
            "Wrote x_sap_invoice_docentry={SapDocEntry} on account.move id={InvoiceId}",
            request.SapDocEntry, invoiceId);

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

        // 3. Write x_sap_invoice_linenum and x_sap_gross_buy_price on each line by position.
        //    Odoo lines are matched to SAP lines by order (first → 0, second → 1, …).
        int linesUpdated = 0;

        for (int i = 0; i < Math.Min(lineIds.Count, request.Lines.Count); i++)
        {
            int odooLineId = lineIds[i];
            var sapLine = request.Lines[i];

            await WriteAsync("account.move.line", odooLineId, new JsonObject
            {
                ["x_sap_invoice_linenum"] = sapLine.SapLineNum,
                ["x_sap_gross_buy_price"] = sapLine.GrossBuyPrice
            });

            _logger.LogDebug(
                "Odoo line id={OdooLineId}: x_sap_invoice_linenum={LineNum}, x_sap_gross_buy_price={GrossBuyPrice}",
                odooLineId, sapLine.SapLineNum, sapLine.GrossBuyPrice);

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

    public async Task<CogsJournalResponse> CreateOrUpdateCogsJournalAsync(CogsJournalRequest request)
    {
        if (!_settings.UseBearerAuth)
            await EnsureAuthenticatedAsync();

        // 4.2 — Find the Odoo invoice by x_sap_invoice_docentry
        var invoiceRecords = await SearchReadAsync("account.move", new JsonArray
        {
            new JsonArray { JsonValue.Create("move_type"), JsonValue.Create("in"), new JsonArray { JsonValue.Create("out_invoice"), JsonValue.Create("out_refund") } },
            new JsonArray { JsonValue.Create("x_sap_invoice_docentry"), JsonValue.Create("="), JsonValue.Create(request.DocEntry) }
        }, new JsonArray
        {
            JsonValue.Create("id"),
            JsonValue.Create("name"),
            JsonValue.Create("invoice_date")
        });

        if (invoiceRecords.Count == 0)
            throw new InvalidOperationException(
                $"Odoo invoice not found for SAP DocEntry={request.DocEntry}. " +
                "Ensure x_sap_invoice_docentry has been written back to Odoo.");

        var invoice = invoiceRecords[0];
        int invoiceId = invoice["id"]!.GetValue<int>();
        string invoiceName = invoice["name"]?.GetValue<string>() ?? "";
        string invoiceDate = invoice["invoice_date"]?.GetValue<string>()
            ?? request.DocDate?.ToString("yyyy-MM-dd")
            ?? DateTime.UtcNow.ToString("yyyy-MM-dd");

        _logger.LogInformation(
            "Found Odoo invoice id={InvoiceId} name={InvoiceName} for SAP DocEntry={DocEntry}",
            invoiceId, invoiceName, request.DocEntry);

        // 4.3 — Match SAP lines to Odoo invoice lines
        var odooLines = await SearchReadAsync("account.move.line", new JsonArray
        {
            new JsonArray { JsonValue.Create("move_id"), JsonValue.Create("="), JsonValue.Create(invoiceId) },
            new JsonArray { JsonValue.Create("display_type"), JsonValue.Create("="), JsonValue.Create("product") }
        }, new JsonArray
        {
            JsonValue.Create("id"),
            JsonValue.Create("product_id"),
            JsonValue.Create("quantity"),
            JsonValue.Create("analytic_distribution"),
            JsonValue.Create("x_sap_invoice_linenum"),
            JsonValue.Create("x_sap_item_code")
        });

        _logger.LogInformation(
            "Found {Count} product line(s) on Odoo invoice id={InvoiceId}",
            odooLines.Count, invoiceId);

        var matchedLines = MatchLinesToOdoo(request.Lines, odooLines);

        // 4.1 — Compute line COGS
        var cogsLines = new List<(double LineCogs, string ItemCode, int? SapLineNum, JsonNode? AnalyticDistribution, string ProductName)>();
        double totalCogs = 0;

        foreach (var (sapLine, odooLine) in matchedLines)
        {
            double lineCogs;
            if (sapLine.StockSum.HasValue)
                lineCogs = sapLine.StockSum.Value;
            else if (sapLine.UnitCost.HasValue)
                lineCogs = sapLine.UnitCost.Value * sapLine.Quantity;
            else
                throw new InvalidOperationException(
                    $"SAP line ItemCode={sapLine.ItemCode} has neither UnitCost nor StockSum.");

            var analyticDist = odooLine?["analytic_distribution"];
            string productName = "";
            if (odooLine?["product_id"] is JsonArray productArr && productArr.Count >= 2)
                productName = productArr[1]?.GetValue<string>() ?? sapLine.ItemCode;
            else
                productName = sapLine.ItemCode;

            cogsLines.Add((lineCogs, sapLine.ItemCode, sapLine.LineNum, analyticDist, productName));
            totalCogs += lineCogs;
        }

        if (totalCogs == 0)
        {
            _logger.LogWarning("Total COGS is zero for SAP DocEntry={DocEntry}. Skipping JE creation.", request.DocEntry);
            return new CogsJournalResponse
            {
                SapDocEntry = request.DocEntry,
                OdooInvoiceId = invoiceId,
                OdooInvoiceName = invoiceName,
                Action = "skipped",
                TotalCogs = 0
            };
        }

        // 4.7 — Generate hash
        string hash = ComputeCogsHash(request);

        // 4.5 — Idempotency check
        var existingJeIds = await SearchAsync("account.move", new JsonArray
        {
            new JsonArray { JsonValue.Create("journal_id"), JsonValue.Create("="), JsonValue.Create(_settings.CogsJournalId) },
            new JsonArray { JsonValue.Create("x_cogs_for_invoice_id"), JsonValue.Create("="), JsonValue.Create(invoiceId) }
        });

        string action;
        int cogsJeId;

        if (existingJeIds.Count > 0)
        {
            cogsJeId = existingJeIds[0];

            // Read existing hash
            var existingJe = await ReadAsync("account.move", cogsJeId, new JsonArray
            {
                JsonValue.Create("x_cogs_import_hash"),
                JsonValue.Create("state")
            });

            string existingHash = existingJe?["x_cogs_import_hash"]?.GetValue<string>() ?? "";

            if (existingHash == hash)
            {
                _logger.LogInformation(
                    "COGS JE id={JeId} already exists with same hash — skipping. DocEntry={DocEntry}",
                    cogsJeId, request.DocEntry);

                return new CogsJournalResponse
                {
                    SapDocEntry = request.DocEntry,
                    OdooInvoiceId = invoiceId,
                    OdooInvoiceName = invoiceName,
                    CogsJournalEntryId = cogsJeId,
                    Action = "skipped",
                    Hash = hash,
                    DebitLineCount = cogsLines.Count,
                    TotalCogs = totalCogs
                };
            }

            // Hash differs — update: Policy A (draft → replace lines → repost)
            _logger.LogInformation(
                "COGS JE id={JeId} exists but hash differs — updating. DocEntry={DocEntry}",
                cogsJeId, request.DocEntry);

            string state = existingJe?["state"]?.GetValue<string>() ?? "";
            if (state == "posted")
            {
                await ExecuteMethodAsync("account.move", "button_draft", new JsonArray { cogsJeId });
                _logger.LogDebug("Reset COGS JE id={JeId} to draft for update", cogsJeId);
            }

            // Delete existing lines
            var existingLineIds = await SearchAsync("account.move.line", new JsonArray
            {
                new JsonArray { JsonValue.Create("move_id"), JsonValue.Create("="), JsonValue.Create(cogsJeId) }
            });

            // Update the JE header with new ref, date, and hash. Write new lines via line_ids.
            var jeUpdatePayload = BuildJeWritePayload(
                invoiceName, request.DocEntry, invoiceDate, hash, invoiceId,
                cogsLines, existingLineIds);

            await WriteAsync("account.move", cogsJeId, jeUpdatePayload);

            // Post the JE
            await ExecuteMethodAsync("account.move", "action_post", new JsonArray { cogsJeId });
            _logger.LogInformation("Updated and posted COGS JE id={JeId}", cogsJeId);

            action = "updated";
        }
        else
        {
            // 4.6 — Create new COGS JE
            _logger.LogInformation(
                "Creating new COGS JE for invoice id={InvoiceId} name={InvoiceName}, DocEntry={DocEntry}",
                invoiceId, invoiceName, request.DocEntry);

            var jePayload = BuildJeCreatePayload(
                invoiceName, request.DocEntry, invoiceDate, hash, invoiceId,
                cogsLines, totalCogs);

            cogsJeId = await CreateAsync("account.move", jePayload);

            // Post the JE
            await ExecuteMethodAsync("account.move", "action_post", new JsonArray { cogsJeId });
            _logger.LogInformation("Created and posted COGS JE id={JeId}", cogsJeId);

            action = "created";
        }

        return new CogsJournalResponse
        {
            SapDocEntry = request.DocEntry,
            OdooInvoiceId = invoiceId,
            OdooInvoiceName = invoiceName,
            CogsJournalEntryId = cogsJeId,
            Action = action,
            Hash = hash,
            DebitLineCount = cogsLines.Count,
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
        return Convert.ToHexStringLower(bytes);
    }

    // ── JE payload builders (4.6) ────────────────────────────────────

    private JsonObject BuildJeCreatePayload(
        string invoiceName, int docEntry, string date, string hash, int invoiceId,
        List<(double LineCogs, string ItemCode, int? SapLineNum, JsonNode? AnalyticDistribution, string ProductName)> cogsLines,
        double totalCogs)
    {
        var lineCommands = new JsonArray();

        // Debit lines (one per invoice line)
        foreach (var (lineCogs, itemCode, sapLineNum, analyticDist, productName) in cogsLines)
        {
            var lineNumLabel = sapLineNum.HasValue ? sapLineNum.Value.ToString() : "?";
            var debitLine = new JsonObject
            {
                ["account_id"] = _settings.CogsAccountId,
                ["debit"] = lineCogs,
                ["credit"] = 0,
                ["name"] = $"COGS | {productName} | INV {invoiceName} | SAP line {lineNumLabel}"
            };

            // 4.4 — Apply analytic distribution from the invoice line
            if (IsValidAnalyticDistribution(analyticDist))
            {
                debitLine["analytic_distribution"] = analyticDist!.DeepClone();
            }

            // Command (0, 0, vals) = create new line
            lineCommands.Add(new JsonArray { 0, 0, debitLine });
        }

        // One credit line (COGS Clearing)
        var creditLine = new JsonObject
        {
            ["account_id"] = _settings.CogsClearingAccountId,
            ["debit"] = 0,
            ["credit"] = totalCogs,
            ["name"] = $"COGS Clearing | INV {invoiceName}"
        };
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
        List<int> existingLineIds)
    {
        var lineCommands = new JsonArray();

        // Delete existing lines: (2, id, 0) = delete
        foreach (int lineId in existingLineIds)
        {
            lineCommands.Add(new JsonArray { 2, lineId, 0 });
        }

        // Recreate debit lines
        double totalCogs = 0;
        foreach (var (lineCogs, itemCode, sapLineNum, analyticDist, productName) in cogsLines)
        {
            totalCogs += lineCogs;
            var lineNumLabel = sapLineNum.HasValue ? sapLineNum.Value.ToString() : "?";
            var debitLine = new JsonObject
            {
                ["account_id"] = _settings.CogsAccountId,
                ["debit"] = lineCogs,
                ["credit"] = 0,
                ["name"] = $"COGS | {productName} | INV {invoiceName} | SAP line {lineNumLabel}"
            };

            if (IsValidAnalyticDistribution(analyticDist))
            {
                debitLine["analytic_distribution"] = analyticDist!.DeepClone();
            }

            lineCommands.Add(new JsonArray { 0, 0, debitLine });
        }

        // Recreate credit line
        var creditLine = new JsonObject
        {
            ["account_id"] = _settings.CogsClearingAccountId,
            ["debit"] = 0,
            ["credit"] = totalCogs,
            ["name"] = $"COGS Clearing | INV {invoiceName}"
        };
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
            var result = await SendJson2Async(model, "create", new JsonObject { ["vals"] = values });
            return result?.GetValue<int>() ?? throw new InvalidOperationException($"Odoo create on {model} returned null.");
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
                    ?? errorJson?["error"]?.GetValue<string>()
                    ?? $"HTTP {(int)response.StatusCode}";
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
