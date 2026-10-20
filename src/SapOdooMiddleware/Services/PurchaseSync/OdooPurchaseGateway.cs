using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
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

/// <summary>A container order (<c>supplier.order</c>) with its match keys.</summary>
public sealed record OdooContainerOrder(int Id, string? Name, string? ContainerNo, string? InvoiceNo);

/// <summary>Journal → company → currency, read once per document.</summary>
public sealed record OdooJournalInfo(int JournalId, int CompanyId, int CompanyCurrencyId, string CompanyCurrency);

/// <summary>The Odoo operations the purchases handlers need, expressed in domain terms.</summary>
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

    Task<int> UpsertSapPoAsync(SapPoHeader po, int? partnerId, int? currencyId);
    Task<List<int>> FindSapPoIdsAsync(IEnumerable<int> docEntries);
    Task<int> UpsertSapGrpoAsync(SapGrpoHeader grpo, IEnumerable<int> poIds, int? orderId);

    Task<List<OdooContainerOrder>> GetContainerOrdersAsync(bool refresh = false);
    Task LinkGrpoToOrderAsync(int orderId, int grpoId);
    Task LinkBillToOrdersAsync(int moveId, IReadOnlyCollection<int> orderIds, IReadOnlyCollection<int> poIds);

    Task NotifyCreditNoteAsync(int moveId);
}

/// <summary>JSON-RPC implementation over <see cref="IOdooRpc"/>.</summary>
public sealed class OdooPurchaseGateway : IOdooPurchaseGateway
{
    private const string OrdersCacheKey = "purchase-sync:container-orders";
    private static readonly string[] MoveFields = ["id", "name", "state", "payment_state", "amount_total", "amount_residual"];

    private readonly IOdooRpc _rpc;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<PurchaseSyncSettings> _settings;
    private readonly ILogger<OdooPurchaseGateway> _logger;

    public OdooPurchaseGateway(
        IOdooRpc rpc,
        IMemoryCache cache,
        IOptionsMonitor<PurchaseSyncSettings> settings,
        ILogger<OdooPurchaseGateway> logger)
    {
        _rpc = rpc;
        _cache = cache;
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
            new JsonArray { "x_sap_cardcode", "=", cardCode.Trim() }
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
        string field = objType == 19 ? "x_sap_credit_docentry" : "x_sap_invoice_docentry";
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
    // x.sap.po / x.sap.grpo
    // ------------------------------------------------------------------

    public async Task<int> UpsertSapPoAsync(SapPoHeader po, int? partnerId, int? currencyId)
    {
        var values = new JsonObject
        {
            ["docentry"] = po.DocEntry,
            ["docnum"] = po.DocNum,
            ["partner_id"] = partnerId.HasValue ? partnerId.Value : (JsonNode?)false,
            ["currency_id"] = currencyId.HasValue ? currencyId.Value : (JsonNode?)false,
            ["doc_total"] = (double)(po.DocTotalFc != 0 ? po.DocTotalFc : po.DocTotal),
            ["doc_date"] = OdooValues.Date(po.DocDate),
            ["due_date"] = po.DocDueDate.HasValue ? OdooValues.Date(po.DocDueDate.Value) : (JsonNode?)false,
            ["status"] = po.Status,
            ["num_at_card"] = (JsonNode?)(po.NumAtCard ?? string.Empty),
        };

        var existing = await _rpc.SearchAsync("x.sap.po", new JsonArray
        {
            new JsonArray { "docentry", "=", po.DocEntry }
        }, limit: 1);

        if (existing.Count > 0)
        {
            await _rpc.WriteAsync("x.sap.po", [existing[0]], values);
            return existing[0];
        }
        return await _rpc.CreateAsync("x.sap.po", values);
    }

    public async Task<List<int>> FindSapPoIdsAsync(IEnumerable<int> docEntries)
    {
        var list = docEntries.Distinct().ToList();
        if (list.Count == 0) return [];
        return await _rpc.SearchAsync("x.sap.po", new JsonArray
        {
            new JsonArray { "docentry", "in", new JsonArray(list.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()) }
        });
    }

    public async Task<int> UpsertSapGrpoAsync(SapGrpoHeader grpo, IEnumerable<int> poIds, int? orderId)
    {
        var lineJson = JsonSerializer.Serialize(grpo.Lines.Select(l => new
        {
            line_num = l.LineNum,
            item_code = l.ItemCode,
            description = l.Dscription,
            quantity = l.Quantity,
            base_type = l.BaseType,
            base_entry = l.BaseEntry,
        }));

        var values = new JsonObject
        {
            ["docentry"] = grpo.DocEntry,
            ["docnum"] = grpo.DocNum,
            ["po_ids"] = new JsonArray { new JsonArray { 6, 0, new JsonArray(poIds.Distinct().Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) } },
            ["ship_ref"] = (JsonNode?)(grpo.ShipRef ?? string.Empty),
            ["doc_date"] = OdooValues.Date(grpo.DocDate),
            ["doc_total_lc"] = (double)grpo.DocTotal,
            ["line_json"] = lineJson,
        };
        if (orderId.HasValue)
            values["order_id"] = orderId.Value;

        var existing = await _rpc.SearchAsync("x.sap.grpo", new JsonArray
        {
            new JsonArray { "docentry", "=", grpo.DocEntry }
        }, limit: 1);

        if (existing.Count > 0)
        {
            await _rpc.WriteAsync("x.sap.grpo", [existing[0]], values);
            return existing[0];
        }
        return await _rpc.CreateAsync("x.sap.grpo", values);
    }

    // ------------------------------------------------------------------
    // Container orders
    // ------------------------------------------------------------------

    public async Task<List<OdooContainerOrder>> GetContainerOrdersAsync(bool refresh = false)
    {
        if (!refresh && _cache.TryGetValue(OrdersCacheKey, out List<OdooContainerOrder>? cached) && cached != null)
            return cached;

        var model = _settings.CurrentValue.ContainerOrderModel;
        var rows = await _rpc.SearchReadAsync(model, new JsonArray
        {
            "|",
            new JsonArray { "container_no", "!=", false },
            new JsonArray { "invoice_no", "!=", false }
        }, ["id", "name", "container_no", "invoice_no"]);

        var orders = rows.Select(r => new OdooContainerOrder(
            r["id"]!.GetValue<int>(),
            OdooValues.String(r["name"]),
            OdooValues.String(r["container_no"]),
            OdooValues.String(r["invoice_no"]))).ToList();

        _cache.Set(OrdersCacheKey, orders, TimeSpan.FromMinutes(2));
        return orders;
    }

    public Task LinkGrpoToOrderAsync(int orderId, int grpoId) =>
        _rpc.WriteAsync(_settings.CurrentValue.ContainerOrderModel, [orderId], new JsonObject
        {
            ["x_sap_grpo_ids"] = new JsonArray { new JsonArray { 4, grpoId, 0 } }
        });

    public async Task LinkBillToOrdersAsync(int moveId, IReadOnlyCollection<int> orderIds, IReadOnlyCollection<int> poIds)
    {
        var values = new JsonObject
        {
            ["x_sap_po_ref_ids"] = new JsonArray { new JsonArray { 6, 0, new JsonArray(poIds.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) } },
        };
        if (orderIds.Count > 0)
            values["x_order_ids"] = new JsonArray { new JsonArray { 6, 0, new JsonArray(orderIds.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) } };

        await _rpc.WriteAsync("account.move", [moveId], values);

        if (orderIds.Count > 0)
        {
            await _rpc.WriteAsync(_settings.CurrentValue.ContainerOrderModel, orderIds, new JsonObject
            {
                ["x_sap_ap_invoice_ids"] = new JsonArray { new JsonArray { 4, moveId, 0 } }
            });
        }
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
