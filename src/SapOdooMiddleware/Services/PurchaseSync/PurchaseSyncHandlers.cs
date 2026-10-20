using System.Text.Json;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.PurchaseSync;

namespace SapOdooMiddleware.Services.PurchaseSync;

/// <summary>Outcome of one queue row.</summary>
public sealed record HandlerResult(bool Success, string Note, string? Code = null, bool Retryable = false)
{
    public static HandlerResult Done(string note) => new(true, note);
    public static HandlerResult Done(object payload) => new(true, JsonSerializer.Serialize(payload));
    public static HandlerResult Fail(string code, string message, bool retryable = false) =>
        new(false, message, code, retryable);
}

/// <summary>
/// The SAP → Odoo handlers of spec v4 §6.1: PO_UPSERT, GRPO_LINK, AP_INVOICE_CREATE,
/// AP_CREDIT_CREATE, AP_INVOICE_CANCEL, AP_CREDIT_CANCEL. SAP is read by SQL, Odoo written by
/// JSON-RPC, the GRPO back-stamp (U_OdooShipmentId) is the one DI-API write.
/// </summary>
public sealed class PurchaseSyncHandlers
{
    public const string NoteBeforeCutover = "BEFORE_CUTOVER";
    public const string NoteCancellationDocSkipped = "CANCELLATION_DOC_SKIPPED";
    public const string NoteNoOrderForShipRef = "NO_ORDER_FOR_SHIPREF";
    public const string ErrCancelAfterAllocation = "CANCEL_AFTER_ALLOCATION";
    public const string ErrPartnerNotMapped = "PARTNER_NOT_MAPPED";

    private readonly ISapPurchaseReader _sap;
    private readonly IOdooPurchaseGateway _odoo;
    private readonly ISapB1Service _sapWriter;
    private readonly IOptionsMonitor<PurchaseSyncSettings> _settings;
    private readonly ILogger<PurchaseSyncHandlers> _logger;

    public PurchaseSyncHandlers(
        ISapPurchaseReader sap,
        IOdooPurchaseGateway odoo,
        ISapB1Service sapWriter,
        IOptionsMonitor<PurchaseSyncSettings> settings,
        ILogger<PurchaseSyncHandlers> logger)
    {
        _sap = sap;
        _odoo = odoo;
        _sapWriter = sapWriter;
        _settings = settings;
        _logger = logger;
    }

    public async Task<HandlerResult> HandleAsync(PurchaseQueueRow row, IccMappings mappings, CancellationToken ct)
    {
        if (row.DocEntry <= 0)
            return HandlerResult.Fail("SP_FAILED_ROW",
                "Row has no DocEntry: it was written by the stored procedure's CATCH block. See its ErrorMessage; nothing to sync.");

        return row.EventType switch
        {
            PurchaseEventTypes.PoUpsert => await PoUpsertAsync(row, mappings, ct),
            PurchaseEventTypes.GrpoLink => await GrpoLinkAsync(row, mappings, ct),
            PurchaseEventTypes.ApInvoiceCreate => await ApCreateAsync(row, 18, mappings, ct),
            PurchaseEventTypes.ApCreditCreate => await ApCreateAsync(row, 19, mappings, ct),
            PurchaseEventTypes.ApInvoiceCancel => await ApCancelAsync(row, 18, mappings, ct),
            PurchaseEventTypes.ApCreditCancel => await ApCancelAsync(row, 19, mappings, ct),
            _ => HandlerResult.Fail("UNKNOWN_EVENT", $"EventType '{row.EventType}' is not a purchase event.")
        };
    }

    private static bool BeforeCutover(IccMappings mappings, DateTime docDate) =>
        mappings.Settings.CutoverDate is { } cutover && DateOnly.FromDateTime(docDate) < cutover;

    // ------------------------------------------------------------------
    // PO_UPSERT
    // ------------------------------------------------------------------

    private async Task<HandlerResult> PoUpsertAsync(PurchaseQueueRow row, IccMappings mappings, CancellationToken ct)
    {
        var po = await _sap.ReadPurchaseOrderAsync(row.DocEntry, ct);
        if (po is null)
            return HandlerResult.Fail("PO_NOT_FOUND", $"OPOR DocEntry {row.DocEntry} does not exist.");

        if (BeforeCutover(mappings, po.DocDate))
            return HandlerResult.Done(NoteBeforeCutover);

        // x.sap.po.upsert resolves the partner from the CardCode and the currency from its code.
        int id = await _odoo.UpsertSapPoAsync(po);
        _logger.LogInformation("PO_UPSERT: OPOR {DocEntry} ({DocNum}) → x.sap.po {Id} status={Status}",
            po.DocEntry, po.DocNum, id, po.Status);

        return HandlerResult.Done(new { x_sap_po_id = id, status = po.Status });
    }

    // ------------------------------------------------------------------
    // GRPO_LINK
    // ------------------------------------------------------------------

    private async Task<HandlerResult> GrpoLinkAsync(PurchaseQueueRow row, IccMappings mappings, CancellationToken ct)
    {
        var grpo = await _sap.ReadGrpoAsync(row.DocEntry, ct);
        if (grpo is null)
            return HandlerResult.Fail("GRPO_NOT_FOUND", $"OPDN DocEntry {row.DocEntry} does not exist.");

        if (grpo.Canceled == "C")
            return HandlerResult.Done(NoteCancellationDocSkipped);

        if (BeforeCutover(mappings, grpo.DocDate))
            return HandlerResult.Done(NoteBeforeCutover);

        // x.sap.grpo.upsert replaces the lines, links the base POs and matches the container order
        // by U_ShipRef (spec §5.3); the quantity check against the order's lines runs in Odoo.
        var upsert = await _odoo.UpsertSapGrpoAsync(grpo);

        string? stampNote = null;
        if (upsert.OrderId is { } orderId)
        {
            string shipmentRef = await _odoo.GetOrderNameAsync(orderId) ?? orderId.ToString();
            if (!string.Equals(grpo.OdooShipmentId?.Trim(), shipmentRef, StringComparison.Ordinal))
            {
                try
                {
                    await _sapWriter.UpdateGrpoOdooShipmentIdAsync(grpo.DocEntry, shipmentRef);
                    await _odoo.MarkGrpoShipmentWrittenAsync(upsert.GrpoId);
                    stampNote = "stamped";
                }
                catch (Exception ex)
                {
                    // The Odoo link is done; the SAP stamp is a convenience. Report, do not fail the row.
                    _logger.LogWarning(ex, "GRPO {DocEntry}: could not write U_OdooShipmentId='{Ref}'.", grpo.DocEntry, shipmentRef);
                    stampNote = $"U_OdooShipmentId not written: {ex.Message}";
                }
            }
            else
            {
                stampNote = "already stamped";
            }
        }

        _logger.LogInformation("GRPO_LINK: OPDN {DocEntry} ShipRef='{ShipRef}' → x.sap.grpo {GrpoId}, order={OrderId}",
            grpo.DocEntry, grpo.ShipRef, upsert.GrpoId, upsert.OrderId);

        return HandlerResult.Done(new
        {
            x_sap_grpo_id = upsert.GrpoId,
            order_id = upsert.OrderId,
            note = upsert.OrderId is null ? NoteNoOrderForShipRef : "linked",
            ship_ref = grpo.ShipRef,
            po_docentries = grpo.PoDocEntries,
            sap_stamp = stampNote,
        });
    }

    // ------------------------------------------------------------------
    // AP_INVOICE_CREATE / AP_CREDIT_CREATE
    // ------------------------------------------------------------------

    private async Task<HandlerResult> ApCreateAsync(PurchaseQueueRow row, int objType, IccMappings mappings, CancellationToken ct)
    {
        string label = objType == 19 ? "A/P Credit Memo" : "A/P Invoice";

        var doc = await _sap.ReadApDocumentAsync(objType, row.DocEntry, ct);
        if (doc is null)
            return HandlerResult.Fail("AP_DOC_NOT_FOUND", $"{label} DocEntry {row.DocEntry} does not exist.");

        if (doc.IsCancellationDocument)
            return HandlerResult.Done(NoteCancellationDocSkipped);

        if (BeforeCutover(mappings, doc.DocDate))
            return HandlerResult.Done(NoteBeforeCutover);

        var settings = mappings.Settings;
        int journalId = settings.BillJournalId
            ?? throw new PurchaseSyncException("BILL_JOURNAL_MISSING",
                "icc.ap.sync.settings.bill_journal_id is empty — set the purchase journal for synced bills.", retryable: true);
        var journal = await _odoo.GetJournalInfoAsync(journalId);

        // Idempotency: an existing posted move means done; an existing draft (a previous run died
        // between create and post) is completed, never duplicated.
        var existing = await _odoo.FindMoveBySapDocEntryAsync(objType, doc.DocEntry);
        if (existing != null && existing.State != "draft")
            return HandlerResult.Done(new { move_id = existing.Id, name = existing.Name, note = "ALREADY_SYNCED", state = existing.State });

        int moveId;
        if (existing != null)
        {
            moveId = existing.Id;
        }
        else
        {
            int partnerId = await _odoo.FindPartnerIdByCardCodeAsync(doc.CardCode)
                ?? throw new PurchaseSyncException(ErrPartnerNotMapped,
                    $"No res.partner has x_sap_cardcode = '{doc.CardCode}' ({doc.CardName}). Set it on the supplier and retry.");

            // Every account and tax must resolve before anything is created (spec §2.6).
            var lines = ApDocumentMapper.ResolveLines(doc, mappings, journal.CompanyCurrency);
            int currencyId = await _odoo.GetCurrencyIdAsync(doc.DocCur);

            int? reversedEntryId = null;
            if (doc.IsCreditMemo && doc.BaseInvoiceDocEntry is { } baseEntry)
                reversedEntryId = (await _odoo.FindMoveBySapDocEntryAsync(18, baseEntry))?.Id;

            var ctx = new ApMoveContext(
                PartnerId: partnerId,
                JournalId: journalId,
                CurrencyId: currencyId,
                CompanyCurrency: journal.CompanyCurrency,
                InvoiceRateIsInverseOfDocRate: _settings.CurrentValue.InvoiceRateIsInverseOfDocRate,
                ReversedEntryId: reversedEntryId);

            var values = ApDocumentMapper.BuildMoveValues(doc, ctx, lines);
            moveId = await _odoo.CreateMoveAsync(values);
            _logger.LogInformation("{Label} {DocEntry} ({DocNum}) → account.move {MoveId} created with {Lines} lines.",
                label, doc.DocEntry, doc.DocNum, moveId, lines.Count);
        }

        // Total check, then post or flag.
        var created = await _odoo.ReadMoveAsync(moveId);
        bool posted = false;
        string? error = null;
        if (ApDocumentMapper.TotalsMatch(doc, journal.CompanyCurrency, created.AmountTotal, settings.TotalTolerance))
        {
            await _odoo.PostMoveAsync(moveId);
            posted = true;
        }
        else
        {
            error = ApDocumentMapper.ErrTotalMismatch;
            decimal expected = ApDocumentMapper.ExpectedTotal(doc, journal.CompanyCurrency);
            await _odoo.SetMoveSyncErrorAsync(moveId, error);
            await SafeMessagePostAsync(moveId,
                $"SAP {label} {doc.DocNum}: total mismatch. SAP {expected:0.00} {doc.DocCur} vs Odoo {created.AmountTotal:0.00} " +
                $"(tolerance {settings.TotalTolerance}). Left in draft for review.");
            _logger.LogWarning("{Label} {DocEntry}: TOTAL_MISMATCH SAP={Expected} Odoo={Actual}; move {MoveId} left in draft.",
                label, doc.DocEntry, expected, created.AmountTotal, moveId);
        }

        // Container and PO links: account.move.sap_match matches the bill's CardCode + ref (NumAtCard)
        // against the orders' invoice numbers and links their POs (spec §5.3). Never fails the row.
        List<int> orderIds = [];
        string? linkNote = null;
        try
        {
            orderIds = await _odoo.SapMatchAsync(moveId);
        }
        catch (Exception ex) when (ex is OdooRpcException or PurchaseSyncException)
        {
            linkNote = $"sap_match failed: {ex.Message}";
            _logger.LogWarning(ex, "{Label} {DocEntry}: account.move.sap_match failed on move {MoveId}.", label, doc.DocEntry, moveId);
        }

        string? creditNoteNote = null;
        if (posted && doc.IsCreditMemo)
        {
            try { await _odoo.NotifyCreditNoteAsync(moveId); }
            catch (OdooRpcException ex)
            {
                creditNoteNote = $"CREDIT_NOTE_ENTRY_NOT_CREATED: {ex.Message}";
                _logger.LogWarning(ex, "Credit memo {DocEntry}: supplier.account.entry.sync_from_credit_note failed for move {MoveId}.",
                    doc.DocEntry, moveId);
            }
        }

        return HandlerResult.Done(new
        {
            move_id = moveId,
            posted,
            error,
            order_ids = orderIds,
            link = linkNote,
            credit_note = creditNoteNote,
        });
    }

    // ------------------------------------------------------------------
    // AP_INVOICE_CANCEL / AP_CREDIT_CANCEL
    // ------------------------------------------------------------------

    private async Task<HandlerResult> ApCancelAsync(PurchaseQueueRow row, int objType, IccMappings mappings, CancellationToken ct)
    {
        string label = objType == 19 ? "A/P Credit Memo" : "A/P Invoice";

        var existing = await _odoo.FindMoveBySapDocEntryAsync(objType, row.DocEntry);
        if (existing is null)
            return HandlerResult.Done(new { note = "NOT_IN_ODOO" });

        switch (existing.State)
        {
            case "draft":
                await _odoo.CancelDraftMoveAsync(existing.Id);
                await _odoo.SetMoveSyncErrorAsync(existing.Id, "CANCELLED_IN_SAP");
                return HandlerResult.Done(new { move_id = existing.Id, note = "DRAFT_CANCELLED" });

            case "cancel":
                return HandlerResult.Done(new { move_id = existing.Id, note = "ALREADY_CANCELLED" });
        }

        bool unreconciled = string.Equals(existing.PaymentState, "not_paid", StringComparison.OrdinalIgnoreCase)
            && Math.Abs(Math.Abs(existing.AmountResidual) - Math.Abs(existing.AmountTotal)) < 0.005m;

        if (!unreconciled)
        {
            await _odoo.SetMoveSyncErrorAsync(existing.Id, ErrCancelAfterAllocation);
            await SafeMessagePostAsync(existing.Id,
                $"SAP {label} DocEntry {row.DocEntry} was cancelled in SAP after this document was reconciled in Odoo. " +
                "Nothing was changed automatically (CANCEL_AFTER_ALLOCATION). Accounts must unreconcile and reverse by hand.");
            _logger.LogWarning("{Label} {DocEntry}: CANCEL_AFTER_ALLOCATION on move {MoveId} (payment_state={PaymentState}).",
                label, row.DocEntry, existing.Id, existing.PaymentState);
            return HandlerResult.Done(new { move_id = existing.Id, note = ErrCancelAfterAllocation });
        }

        var reversalIds = await _odoo.ReverseMoveAsync(
            existing.Id,
            reason: $"SAP {label} {row.DocNum ?? row.DocEntry} cancelled",
            date: DateTime.UtcNow.Date,
            journalId: mappings.Settings.BillJournalId);
        await _odoo.SetMoveSyncErrorAsync(existing.Id, "CANCELLED_IN_SAP");

        _logger.LogInformation("{Label} {DocEntry}: move {MoveId} reversed → {Reversals}.",
            label, row.DocEntry, existing.Id, string.Join(",", reversalIds));

        return HandlerResult.Done(new { move_id = existing.Id, note = "REVERSED", reversal_ids = reversalIds });
    }

    private async Task SafeMessagePostAsync(int moveId, string body)
    {
        try { await _odoo.MessagePostAsync("account.move", moveId, body); }
        catch (OdooRpcException ex) { _logger.LogDebug(ex, "message_post failed on account.move {MoveId}", moveId); }
    }
}
