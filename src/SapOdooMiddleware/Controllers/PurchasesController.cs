using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.PurchaseSync;
using SapOdooMiddleware.Services.PurchaseSync;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Purchases sync operations (spec v4 §6.5): the backfill from the cut-over date and a mapping-cache
/// refresh. The SAP → Odoo flow itself runs in <see cref="PurchaseSyncQueueProcessor"/>.
/// </summary>
[ApiController]
[Route("api/purchases")]
public class PurchasesController : ControllerBase
{
    private static readonly string[] DefaultTypes = ["22", "20", "18", "19"];

    private readonly ISapPurchaseReader _sap;
    private readonly PurchaseQueueRepository _queue;
    private readonly IIccMappingCache _mappings;
    private readonly ILogger<PurchasesController> _logger;

    public PurchasesController(
        ISapPurchaseReader sap,
        PurchaseQueueRepository queue,
        IIccMappingCache mappings,
        ILogger<PurchasesController> logger)
    {
        _sap = sap;
        _queue = queue;
        _mappings = mappings;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/purchases/backfill?from=2026-11-01&amp;types=22,20,18,19
    /// DRY RUN: a CSV of the SAP documents dated on or after <c>from</c> (never before the control
    /// centre's cut-over date) with whether each one is already queued. Changes nothing.
    /// </summary>
    [HttpGet("backfill")]
    public async Task<IActionResult> BackfillDryRun(
        [FromQuery] string? from,
        [FromQuery] string? types,
        CancellationToken ct)
    {
        var plan = await PlanAsync(from, types, ct);
        if (plan.Error != null)
            return BadRequest(ApiResponse<string>.Fail(plan.Error));

        var sb = new StringBuilder();
        sb.AppendLine("object_type,event_type,doc_entry,doc_num,card_code,num_at_card,doc_date,doc_total,doc_cur,queue_state");
        foreach (var c in plan.Candidates)
        {
            bool queued = plan.Queued.Contains((c.ObjectType, c.DocEntry));
            sb.Append(c.ObjectType).Append(',')
              .Append(c.EventType).Append(',')
              .Append(c.DocEntry).Append(',')
              .Append(c.DocNum).Append(',')
              .Append(Csv(c.CardCode)).Append(',')
              .Append(Csv(c.NumAtCard)).Append(',')
              .Append(c.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
              .Append(c.DocTotal.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
              .Append(c.DocCur).Append(',')
              .Append(queued ? "queued" : "new")
              .AppendLine();
        }

        _logger.LogInformation("Purchases backfill dry run from {From}: {Count} candidates, {Queued} already queued.",
            plan.From, plan.Candidates.Count, plan.Candidates.Count(c => plan.Queued.Contains((c.ObjectType, c.DocEntry))));

        var fileName = $"purchases-backfill-{plan.From:yyyyMMdd}.csv";
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", fileName);
    }

    /// <summary>
    /// POST /api/purchases/backfill?from=2026-11-01&amp;types=22,20,18,19
    /// Queues every candidate that is not already queued (TransType 'A', the matching EventType).
    /// The worker then processes them like live events; nothing before the cut-over date is queued.
    /// </summary>
    [HttpPost("backfill")]
    public async Task<IActionResult> BackfillEnqueue(
        [FromQuery] string? from,
        [FromQuery] string? types,
        CancellationToken ct)
    {
        var plan = await PlanAsync(from, types, ct);
        if (plan.Error != null)
            return BadRequest(ApiResponse<string>.Fail(plan.Error));

        var queuedNow = new List<object>();
        int skipped = 0;
        foreach (var c in plan.Candidates)
        {
            if (plan.Queued.Contains((c.ObjectType, c.DocEntry)))
            {
                skipped++;
                continue;
            }
            int id = await _queue.EnqueueAsync(c, ct);
            queuedNow.Add(new { queue_id = id, c.ObjectType, c.EventType, c.DocEntry, c.DocNum });
        }

        _logger.LogInformation("Purchases backfill from {From}: queued {Queued}, skipped {Skipped} already queued.",
            plan.From, queuedNow.Count, skipped);

        return Ok(ApiResponse<object>.Ok(new
        {
            from = plan.From.ToString("yyyy-MM-dd"),
            cutover_date = plan.Cutover.ToString("yyyy-MM-dd"),
            candidates = plan.Candidates.Count,
            queued = queuedNow.Count,
            skipped_already_queued = skipped,
            entries = queuedNow,
        }));
    }

    /// <summary>POST /api/purchases/mappings/refresh — drops the cached control-centre mappings.</summary>
    [HttpPost("mappings/refresh")]
    public async Task<IActionResult> RefreshMappings(CancellationToken ct)
    {
        _mappings.Invalidate();
        var m = await _mappings.GetAsync(ct);
        return Ok(ApiResponse<object>.Ok(new
        {
            loaded_at = m.LoadedAt,
            cutover_date = m.Settings.CutoverDate?.ToString("yyyy-MM-dd"),
            bill_journal_id = m.Settings.BillJournalId,
            total_tolerance = m.Settings.TotalTolerance,
            small_balance_limit = m.Settings.SmallBalanceLimit,
            vat_groups = m.TaxByVatGroup.Count,
            item_groups = m.AccountByItemGroup.Count,
            gl_accounts = m.AccountByGlCode.Count,
        }));
    }

    // ------------------------------------------------------------------

    private sealed record BackfillPlan(
        DateOnly From,
        DateOnly Cutover,
        List<SapBackfillCandidate> Candidates,
        HashSet<(string ObjectType, int DocEntry)> Queued,
        string? Error)
    {
        public static BackfillPlan Failed(string error) =>
            new(default, default, [], [], error);
    }

    private async Task<BackfillPlan> PlanAsync(string? from, string? types, CancellationToken ct)
    {
        IccMappings mappings;
        try { mappings = await _mappings.GetAsync(ct); }
        catch (PurchaseSyncException ex) { return BackfillPlan.Failed(ex.Message); }

        if (mappings.Settings.CutoverDate is not { } cutover)
            return BackfillPlan.Failed("CUTOVER_NOT_SET: icc.ap.sync.settings.cutover_date is empty; the backfill never runs before cut-over.");

        DateOnly fromDate = cutover;
        if (!string.IsNullOrWhiteSpace(from))
        {
            if (!DateOnly.TryParseExact(from.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out fromDate))
                return BackfillPlan.Failed("from must be yyyy-MM-dd.");
            if (fromDate < cutover)
                return BackfillPlan.Failed($"from {fromDate:yyyy-MM-dd} is before the cut-over date {cutover:yyyy-MM-dd}; nothing before cut-over is backfilled.");
        }

        var typeList = string.IsNullOrWhiteSpace(types)
            ? DefaultTypes
            : types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unknown = typeList.Where(t => Array.IndexOf(DefaultTypes, t) < 0).ToList();
        if (unknown.Count > 0)
            return BackfillPlan.Failed($"Unknown object type(s) {string.Join(",", unknown)}; allowed: 22, 20, 18, 19.");

        var candidates = await _sap.ListBackfillCandidatesAsync(fromDate.ToDateTime(TimeOnly.MinValue), typeList, ct);
        var queued = await _queue.GetQueuedAddKeysAsync(typeList, ct);
        return new BackfillPlan(fromDate, cutover, candidates, queued, null);
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        bool needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n');
        return needsQuotes ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
