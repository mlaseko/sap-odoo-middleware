using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.PurchaseSync;

namespace SapOdooMiddleware.Services.PurchaseSync;

/// <summary>A synced bill or credit note as seen in Odoo.</summary>
public sealed record OdooMoveInfo(
    int Id,
    string State,
    string? PaymentState,
    decimal AmountTotal,
    decimal AmountResidual,
    string? Name);

/// <summary>Result of <c>x.sap.grpo.upsert</c>: the record and the container order it linked to, if any.</summary>
public sealed record OdooGrpoUpsertResult(int GrpoId, int? OrderId);

/// <summary>Journal → company → currency, read once per document.</summary>
public sealed record OdooJournalInfo(int JournalId, int CompanyId, int CompanyCurrencyId, string CompanyCurrency);

/// <summary>
/// The Odoo operations the purchases handlers need, expressed in domain terms. The reference models
/// and the container match are owned by the Odoo module <c>molas_sap_purchase</c>, which exposes
/// <c>x.sap.po.upsert(vals)</c>, <c>x.sap.grpo.upsert(vals, lines)</c> and
/// <c>account.move.sap_match(move_id)</c>; the middleware calls those rather than writing the links itself.
/// </summary>
public interface IOdooPurchaseGateway
{
    Task<int?> FindPartnerIdByCardCodeAsync(string cardCode);
    Task<int> GetCurrencyIdAsync(string isoCode);
    Task<OdooJournalInfo> GetJournalInfoAsync(int journalId);

    Task<OdooMoveInfo?> FindMoveBySapDocEntryAsync(int objType, int docEntry);
    Task<OdooMoveInfo> ReadMoveAsync(int moveId);
    Task<int> CreateMoveAsync(JsonObject values);
    Task PostMoveAsync(int moveId);
    Task CancelDraftMoveAsync(int moveId);
    Task<List<int>> ReverseMoveAsync(int moveId, string reason, DateTime date, int? journalId);
    Task SetMoveSyncErrorAsync(int moveId, string? code);
    Task MessagePostAsync(string model, int id, string body);

    /// <summary><c>x.sap.po.upsert(vals)</c> → the record id.</summary>
    Task<int> UpsertSapPoAsync(SapPoHeader po);

    /// <summary><c>x.sap.grpo.upsert(vals, lines)</c> → the record id and the linked order id.</summary>
    Task<OdooGrpoUpsertResult> UpsertSapGrpoAsync(SapGrpoHeader grpo);

    /// <summary>Sets <c>x.sap.grpo.odoo_shipment_written</c> after the SAP stamp succeeded.</summary>
    Task MarkGrpoShipmentWrittenAsync(int grpoId);

    /// <summary>The container order's reference (<c>supplier.order.name</c>), used as <c>U_OdooShipmentId</c>.</summary>
    Task<string?> GetOrderNameAsync(int orderId);

    /// <summary><c>account.move.sap_match(move_id)</c> → the ids of the container orders linked.</summary>
    Task<List<int>> SapMatchAsync(int moveId);

    Task NotifyCreditNoteAsync(int moveId);
}

/// <summary>JSON-RPC implementation over <see cref="IOdooRpc"/>.</summary>
public sealed class OdooPurchaseGateway : IOdooPurchaseGateway
{
    private static readonly string[] MoveFields = ["id", "name", "state", "payment_state", "amount_total", "amount_residual"];

    private readonly IOdooRpc _rpc;
    private readonly IOptionsMonitor<PurchaseSyncSettings> _settings;
    private readonly ILogger<OdooPurchaseGateway> _logger;

    public OdooPurchaseGateway(
        IOdooRpc rpc,
        IOptionsMonitor<PurchaseSyncSettings> settings,
        ILogger<OdooPurchaseGateway> logger)
    {
        _rpc = rpc;
        _settings = settings;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Partners, currencies, journals
    // ------------------------------------------------------------------

    public async Task<int?> FindPartnerIdByCardCodeAsync(string cardCode)
    {
        var ids = await _rpc.SearchAsync("res.partner", new JsonArray
        {
            new JsonArray { "x_sap_cardcode", "=ilike", cardCode.Trim() }
        }, limit: 1);
        return ids.Count > 0 ? ids[0] : null;
    }

    public async Task<int> GetCurrencyIdAsync(string isoCode)
    {
        var rows = await _rpc.SearchReadAsync("res.currency", new JsonArray
        {
            new JsonArray { "name", "=", isoCode.Trim().ToUpperInvariant() },
            new JsonArray { "active", "in", new JsonArray { true, false } }
        }, ["id", "active"], limit: 1);

        if (rows.Count == 0)
            throw new PurchaseSyncException("CURRENCY_NOT_FOUND", $"Odoo has no currency '{isoCode}'.");

        if (rows[0]["active"] is JsonValue v && v.TryGetValue<bool>(out var active) && !active)
            throw new PurchaseSyncException("CURRENCY_INACTIVE", $"Odoo currency '{isoCode}' is archived; activate it.");

        return rows[0]["id"]!.GetValue<int>();
    }

    public async Task<OdooJournalInfo> GetJournalInfoAsync(int journalId)
    {
        var journal = await _rpc.ReadAsync("account.journal", journalId, ["id", "company_id", "type"])
            ?? throw new PurchaseSyncException("BILL_JOURNAL_MISSING", $"account.journal {journalId} does not exist.");

        var companyId = OdooValues.Many2OneId(journal["company_id"])
            ?? throw new PurchaseSyncException("BILL_JOURNAL_MISSING", $"account.journal {journalId} has no company.");

        var company = await _rpc.ReadAsync("res.company", companyId, ["id", "currency_id"])
            ?? throw new PurchaseSyncException("BILL_JOURNAL_MISSING", $"res.company {companyId} not readable.");

        var currencyId = OdooValues.Many2OneId(company["currency_id"])
            ?? throw new PurchaseSyncException("BILL_JOURNAL_MISSING", $"res.company {companyId} has no currency.");

        var currency = await _rpc.ReadAsync("res.currency", currencyId, ["id", "name"]);
        var code = OdooValues.String(currency?["name"]) ?? "TZS";

        return new OdooJournalInfo(journalId, companyId, currencyId, code);
    }

    // ------------------------------------------------------------------
    // Bills and credit notes
    // ------------------------------------------------------------------

    public async Task<OdooMoveInfo?> FindMoveBySapDocEntryAsync(int objType, int docEntry)
    {
        string field = objType == 19 ? ApDocumentMapper.CreditDocEntryField : ApDocumentMapper.InvoiceDocEntryField;
        string moveType = objType == 19 ? "in_refund" : "in_invoice";

        var rows = await _rpc.SearchReadAsync("account.move", new JsonArray
        {
            new JsonArray { field, "=", docEntry },
            new JsonArray { "move_type", "=", moveType }
        }, MoveFields, limit: 1, order: "id desc");

        return rows.Count == 0 ? null : ToMoveInfo(rows[0]);
    }

    public async Task<OdooMoveInfo> ReadMoveAsync(int moveId)
    {
        var row = await _rpc.ReadAsync("account.move", moveId, MoveFields)
            ?? throw new PurchaseSyncException("MOVE_NOT_FOUND", $"account.move {moveId} vanished.", retryable: true);
        return ToMoveInfo(row);
    }

    public Task<int> CreateMoveAsync(JsonObject values) => _rpc.CreateAsync("account.move", values);

    public Task PostMoveAsync(int moveId) => _rpc.CallAsync("account.move", "action_post", [moveId]);

    public Task CancelDraftMoveAsync(int moveId) => _rpc.CallAsync("account.move", "button_cancel", [moveId]);

    public async Task<List<int>> ReverseMoveAsync(int moveId, string reason, DateTime date, int? journalId)
    {
        var wizardValues = new JsonObject
        {
            ["move_ids"] = new JsonArray { new JsonArray { 6, 0, new JsonArray { moveId } } },
            ["reason"] = reason,
            ["date_mode"] = "custom",
            ["date"] = OdooValues.Date(date),
        };
        if (journalId.HasValue)
            wizardValues["journal_id"] = journalId.Value;

        int wizardId = await _rpc.CreateAsync("account.move.reversal", wizardValues);
        await _rpc.CallAsync("account.move.reversal", "reverse_moves", [wizardId]);

        return await _rpc.SearchAsync("account.move", new JsonArray
        {
            new JsonArray { "reversed_entry_id", "=", moveId }
        }, order: "id desc");
    }

    public Task SetMoveSyncErrorAsync(int moveId, string? code) =>
        _rpc.WriteAsync("account.move", [moveId], new JsonObject { ["x_sap_sync_error"] = (JsonNode?)code });

    public Task MessagePostAsync(string model, int id, string body) =>
        _rpc.CallAsync(model, "message_post", [id], new JsonObject { ["body"] = body });

    // ------------------------------------------------------------------
    // x.sap.po / x.sap.grpo (owned by molas_sap_purchase; we call its upsert methods)
    // ------------------------------------------------------------------

    public async Task<int> UpsertSapPoAsync(SapPoHeader po)
    {
        var vals = new JsonObject
        {
            ["docentry"] = po.DocEntry,
            ["docnum"] = po.DocNum.ToString(),
            ["cardcode"] = po.CardCode,
            ["currency"] = po.DocCur,
            ["doc_total"] = (double)(po.DocTotalFc != 0 ? po.DocTotalFc : po.DocTotal),
            ["doc_date"] = OdooValues.Date(po.DocDate),
            ["due_date"] = po.DocDueDate.HasValue ? OdooValues.Date(po.DocDueDate.Value) : (JsonNode?)false,
            ["status"] = po.Status,
            ["comments"] = (JsonNode?)(po.NumAtCard ?? string.Empty),
        };

        var result = await _rpc.CallModelAsync("x.sap.po", "upsert", new JsonObject { ["vals"] = vals });
        return result?.GetValue<int>()
            ?? throw new PurchaseSyncException("UPSERT_FAILED", "x.sap.po.upsert returned no id.", retryable: true);
    }

    public async Task<OdooGrpoUpsertResult> UpsertSapGrpoAsync(SapGrpoHeader grpo)
    {
        var vals = new JsonObject
        {
            ["docentry"] = grpo.DocEntry,
            ["docnum"] = grpo.DocNum.ToString(),
            ["cardcode"] = grpo.CardCode,
            ["ship_ref"] = (JsonNode?)(grpo.ShipRef ?? string.Empty),
            ["doc_date"] = OdooValues.Date(grpo.DocDate),
            ["doc_total_lc"] = (double)grpo.DocTotal,
            ["po_docentries"] = new JsonArray(grpo.PoDocEntries.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
        };

        var lines = new JsonArray();
        foreach (var l in grpo.Lines)
        {
            lines.Add(new JsonObject
            {
                ["line_num"] = l.LineNum,
                ["item_code"] = l.ItemCode,
                ["name"] = (JsonNode?)(l.Dscription ?? string.Empty),
                ["quantity"] = (double)l.Quantity,
            });
        }

        var result = await _rpc.CallModelAsync("x.sap.grpo", "upsert", new JsonObject
        {
            ["vals"] = vals,
            ["lines"] = lines,
        });

        var obj = result as JsonObject
            ?? throw new PurchaseSyncException("UPSERT_FAILED", "x.sap.grpo.upsert returned no record.", retryable: true);

        int grpoId = obj["id"]?.GetValue<int>()
            ?? throw new PurchaseSyncException("UPSERT_FAILED", "x.sap.grpo.upsert returned no id.", retryable: true);
        int? orderId = OdooValues.Many2OneId(obj["order_id"]);
        return new OdooGrpoUpsertResult(grpoId, orderId);
    }

    public Task MarkGrpoShipmentWrittenAsync(int grpoId) =>
        _rpc.WriteAsync("x.sap.grpo", [grpoId], new JsonObject { ["odoo_shipment_written"] = true });

    public async Task<string?> GetOrderNameAsync(int orderId)
    {
        var row = await _rpc.ReadAsync(_settings.CurrentValue.ContainerOrderModel, orderId, ["id", "name"]);
        return OdooValues.String(row?["name"]);
    }

    public async Task<List<int>> SapMatchAsync(int moveId)
    {
        var result = await _rpc.CallModelAsync("account.move", "sap_match", new JsonObject { ["move_id"] = moveId });
        return result is JsonArray arr
            ? arr.Select(n => n!.GetValue<int>()).ToList()
            : [];
    }

    public Task NotifyCreditNoteAsync(int moveId) =>
        _rpc.CallModelAsync("supplier.account.entry", "sync_from_credit_note", new JsonObject
        {
            ["move_ids"] = new JsonArray { moveId }
        });

    // ------------------------------------------------------------------

    private static OdooMoveInfo ToMoveInfo(JsonObject row) => new(
        Id: row["id"]!.GetValue<int>(),
        State: OdooValues.String(row["state"]) ?? "draft",
        PaymentState: OdooValues.String(row["payment_state"]),
        AmountTotal: OdooValues.Decimal(row["amount_total"], 0m),
        AmountResidual: OdooValues.Decimal(row["amount_residual"], 0m),
        Name: OdooValues.String(row["name"]));
}
