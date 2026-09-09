using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Pricing;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Read-only pricing diagnostics: replays the REAL <see cref="PricingCalculator"/>
/// for one Liqui Moly article so the full derivation can be inspected from a
/// terminal (curl) — inputs, band convergence, ratios, rounding, VAT split — and
/// compared against the prices actually stored in Neon.
/// </summary>
[ApiController]
[Route("api/pricing")]
public class PricingTraceController : ControllerBase
{
    private readonly INeonProductRepository _neon;
    private readonly IStagingDocumentLineRepository _stagingLines;
    private readonly IPricingCalculator _pricing;
    private readonly PricingSettings _pricingSettings;
    private readonly ILubesPricingRepository _pricingRepo;
    private readonly ILubesRepriceService _reprice;
    private readonly LubesBulkRepriceJobService _bulkJobs;
    private readonly ILogger<PricingTraceController> _logger;

    public PricingTraceController(
        INeonProductRepository neon,
        IStagingDocumentLineRepository stagingLines,
        IPricingCalculator pricing,
        IOptions<PricingSettings> pricingSettings,
        ILubesPricingRepository pricingRepo,
        ILubesRepriceService reprice,
        LubesBulkRepriceJobService bulkJobs,
        ILogger<PricingTraceController> logger)
    {
        _neon = neon;
        _stagingLines = stagingLines;
        _pricing = pricing;
        _pricingSettings = pricingSettings.Value;
        _pricingRepo = pricingRepo;
        _reprice = reprice;
        _bulkJobs = bulkJobs;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/pricing/trace?article_number=4702&amp;eur_cost=&amp;rate=
    /// Full pricing derivation for one article (Lubes ItemCode == LM article number):
    /// <list type="bullet">
    ///   <item>EUR cost — from <c>eur_cost</c> when given, else the latest extracted
    ///   invoice line carrying this article (the source document is reported).</item>
    ///   <item>Category resolution — SAP group first (authoritative), Odoo category
    ///   fallback, exactly like provisioning.</item>
    ///   <item>The calculator's band iterations, ratios, incl-VAT tiers, Maasai
    ///   derivation, and net prices.</item>
    ///   <item>Comparison against the prices stored in Neon (PL1-4).</item>
    /// </list>
    /// Nothing is written — pure diagnostics.
    /// </summary>
    [HttpGet("trace")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Trace(
        [FromQuery(Name = "article_number")] string? articleNumber,
        [FromQuery(Name = "eur_cost")] decimal? eurCost = null,
        [FromQuery(Name = "rate")] decimal? rate = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(articleNumber))
            return BadRequest(ApiResponse<object>.Fail("article_number query parameter is required."));
        var article = articleNumber.Trim();

        // 1. Neon product state (group, category, stored prices). Lubes ItemCode == article number.
        var snapshot = await _neon.GetPricingSnapshotAsync(article, ct);

        // 2. EUR cost: explicit param, else the latest extracted invoice line.
        object? invoiceSource = null;
        if (eurCost is null)
        {
            var hit = await _stagingLines.FindLatestByArticleAsync(article, ct);
            if (hit is { } h)
            {
                eurCost = h.Line.UnitPrice;
                invoiceSource = new
                {
                    document_id = h.Line.DocumentId,
                    line_id = h.Line.Id,
                    description = h.Line.Description,
                    quantity = h.Line.Quantity,
                    eur_unit_price = h.Line.UnitPrice,
                    review_status = h.Line.ReviewStatus,
                    created_sku = h.Line.CreatedSku,
                    invoice_uploaded_at = h.UploadedAt,
                    review_url = $"/documents/{h.Line.DocumentId}",
                };
            }
        }

        if (snapshot is null && eurCost is null)
            return NotFound(ApiResponse<object>.Fail(
                $"Article '{article}' has no NeonProducts row and no extracted invoice line — " +
                "nothing to trace. Pass eur_cost= to run a what-if computation."));

        // 3. Category resolution — mirrors LubesItemProvisioningService step 5.
        string? category = null;
        string resolutionPath;
        if (snapshot?.ItemGroupCode is int grp
            && _pricing.TryPricingBandForSapGroup(grp) is string bandFromGroup)
        {
            category = bandFromGroup;
            resolutionPath = $"SAP group {grp} ({snapshot.ItemGroupName ?? "?"}) → band '{bandFromGroup}' (authoritative)";
        }
        else if (!string.IsNullOrWhiteSpace(snapshot?.OdooCategoryName))
        {
            try
            {
                category = _pricing.ResolvePricingCategory(snapshot!.OdooCategoryName);
                resolutionPath =
                    $"SAP group {(snapshot.ItemGroupCode?.ToString() ?? "none")} has no band; " +
                    $"Odoo category '{snapshot.OdooCategoryName}' → band '{category}'";
            }
            catch (InvalidOperationException ex)
            {
                resolutionPath = $"Unresolvable: {ex.Message}";
            }
        }
        else
        {
            resolutionPath = "No SAP group band and no Odoo category on the Neon row.";
        }

        var effectiveRate = rate ?? _pricingSettings.EurTzsRate;

        // 4. Replay the real calculator when we have both inputs.
        PricingTrace? trace = null;
        string? traceError = null;
        if (eurCost is > 0m && category is not null)
        {
            try
            {
                trace = _pricing.ComputeNetPricesWithTrace(eurCost.Value * effectiveRate, category);
            }
            catch (Exception ex)
            {
                traceError = ex.Message;
            }
        }
        else if (category is null)
        {
            traceError = "Pricing category could not be resolved — see category_resolution.";
        }
        else
        {
            traceError = "No EUR cost available (no invoice line found; pass eur_cost=).";
        }

        // 5. Compare recomputed net prices with what Neon actually stores.
        object? comparison = null;
        if (trace is not null && snapshot is not null && snapshot.StoredNetPrices.Count > 0)
        {
            static object Cmp(decimal computed, Dictionary<int, decimal> stored, int pl) => new
            {
                computed_net = Math.Round(computed, 2),
                stored_net = stored.TryGetValue(pl, out var s) ? (decimal?)Math.Round(s, 2) : null,
                matches = stored.TryGetValue(pl, out var s2) && Math.Abs(s2 - computed) < 0.05m,
            };
            comparison = new
            {
                pl1_retail = Cmp(trace.Net.Retail, snapshot.StoredNetPrices, 1),
                pl2_dealer = Cmp(trace.Net.Dealer, snapshot.StoredNetPrices, 2),
                pl3_super_dealer = Cmp(trace.Net.SuperDealer, snapshot.StoredNetPrices, 3),
                pl4_maasai = Cmp(trace.Net.Maasai, snapshot.StoredNetPrices, 4),
                note = "A mismatch usually means the item was priced at a different EUR cost or " +
                       "EUR→TZS rate than today's inputs, or its group/category changed since creation.",
            };
        }

        return Ok(ApiResponse<object>.Ok(new
        {
            article_number = article,
            neon_product = snapshot is null ? null : new
            {
                snapshot.ItemCode,
                snapshot.ItemName,
                snapshot.ItemGroupCode,
                snapshot.ItemGroupName,
                snapshot.OdooCategoryName,
                snapshot.SapStatus,
                snapshot.SyncedAt,
                stored_net_prices = snapshot.StoredNetPrices,
            },
            invoice_source = invoiceSource,
            inputs = new
            {
                eur_cost = eurCost,
                eur_tzs_rate = effectiveRate,
                rate_source = rate is null ? "Pricing:EurTzsRate config default" : "query parameter",
                cif_cost_tzs = eurCost is > 0m ? eurCost.Value * effectiveRate : (decimal?)null,
            },
            category_resolution = new { pricing_category = category, path = resolutionPath },
            trace,
            trace_error = traceError,
            comparison,
        }));
    }

    // ── EUR→TZS rate (runtime-editable) ──────────────────────────────

    /// <summary>GET /api/pricing/rate — the effective EUR→TZS rate and its source.</summary>
    [HttpGet("rate")]
    public async Task<IActionResult> GetRate(CancellationToken ct)
        => Ok(ApiResponse<EffectiveRate>.Ok(await _pricingRepo.GetEffectiveRateAsync(ct)));

    /// <summary>PUT /api/pricing/rate — sets the runtime rate (used by provisioning AND repricing).</summary>
    [HttpPut("rate")]
    public async Task<IActionResult> SetRate([FromBody] RateUpdateRequest request, CancellationToken ct)
    {
        if (request.Rate <= 0m)
            return BadRequest(ApiResponse<EffectiveRate>.Fail("rate must be greater than zero."));
        await _pricingRepo.SetRateAsync(request.Rate, ct);
        _logger.LogInformation("EUR→TZS rate set to {Rate} via API/UI.", request.Rate);
        return Ok(ApiResponse<EffectiveRate>.Ok(await _pricingRepo.GetEffectiveRateAsync(ct)));
    }

    // ── Single-item reprice ──────────────────────────────────────────

    /// <summary>
    /// POST /api/pricing/reprice
    /// Repriced from a new EUR purchase price through the EXISTING pricing workflow
    /// (band resolution, calculator, VAT split). <c>dry_run=true</c> (default)
    /// returns the before→after preview with ±30% warnings; <c>dry_run=false</c>
    /// posts to SAP (PL1-4), Neon, the audit log, and Odoo (best-effort).
    /// </summary>
    [HttpPost("reprice")]
    public async Task<IActionResult> Reprice([FromBody] RepriceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ItemCode))
            return BadRequest(ApiResponse<object>.Fail("item_code is required."));

        if (request.DryRun)
        {
            var preview = await _reprice.PreviewAsync(
                request.ItemCode.Trim(), request.EurCost, request.Rate, includeTrace: true, ct);
            return Ok(ApiResponse<RepricePreviewLine>.Ok(preview));
        }

        var result = await _reprice.ApplyAsync(
            request.ItemCode.Trim(), request.EurCost, request.Rate, "manual", request.Note, ct);
        return result.Applied
            ? Ok(ApiResponse<RepriceApplyResult>.Ok(result))
            : BadRequest(ApiResponse<RepriceApplyResult>.Fail(result.Error ?? "Reprice failed."));
    }

    /// <summary>GET /api/pricing/history?item_code= — the price-change audit trail for one item.</summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery(Name = "item_code")] string? itemCode,
        [FromQuery(Name = "limit")] int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
            return BadRequest(ApiResponse<object>.Fail("item_code is required."));
        var history = await _pricingRepo.GetHistoryAsync(itemCode.Trim(), Math.Clamp(limit, 1, 200), ct);
        return Ok(ApiResponse<IReadOnlyList<PriceChangeEntry>>.Ok(history));
    }

    // ── Bulk reprice ─────────────────────────────────────────────────

    private const int BulkDryRunCap = 500;

    /// <summary>
    /// POST /api/pricing/reprice/bulk
    /// Items come from an explicit list (Excel upload) OR a filter
    /// (<c>sap_group</c> / <c>pricing_category</c> — every item with a stored EUR
    /// cost in that group, recomputed at <c>rate</c> or the current rate).
    /// <c>dry_run=true</c> previews all lines synchronously (≤500 items);
    /// <c>dry_run=false</c> starts the background job — poll GET /reprice/bulk.
    /// </summary>
    [HttpPost("reprice/bulk")]
    public async Task<IActionResult> BulkReprice([FromBody] BulkRepriceRequest request, CancellationToken ct)
    {
        List<RepriceItemInput> items;
        string source;

        if (request.Items is { Count: > 0 })
        {
            items = request.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.ItemCode))
                .Select(i => new RepriceItemInput(i.ItemCode.Trim(), i.EurCost))
                .ToList();
            source = "list";
        }
        else
        {
            var groups = ResolveFilterGroups(request.SapGroup, request.PricingCategory);
            if (groups is null)
                return BadRequest(ApiResponse<object>.Fail(
                    "Provide items[], or a filter: sap_group or a known pricing_category " +
                    "(Engine Oils, Additives, Vehicle Care, ...)."));
            var candidates = await _pricingRepo.GetRepriceCandidatesAsync(
                groups.Count > 0 ? groups : null, ct);
            items = candidates.Select(c => new RepriceItemInput(c.ItemCode, c.LastEurCost)).ToList();
            source = "filter";
        }

        if (items.Count == 0)
            return BadRequest(ApiResponse<object>.Fail(
                "No items to reprice. Filter mode only covers items with a stored EUR cost " +
                "(stamped by provisioning or a previous reprice)."));

        if (request.DryRun)
        {
            if (items.Count > BulkDryRunCap)
                return BadRequest(ApiResponse<object>.Fail(
                    $"{items.Count} items exceed the {BulkDryRunCap}-item preview cap — narrow the filter " +
                    "(bulk APPLY has no cap; preview a subset first)."));

            var previews = new List<RepricePreviewLine>(items.Count);
            foreach (var i in items)
                previews.Add(await _reprice.PreviewAsync(i.ItemCode, i.EurCost, request.Rate, includeTrace: false, ct));

            return Ok(ApiResponse<object>.Ok(new
            {
                total = previews.Count,
                applicable = previews.Count(p => p.CanApply),
                with_warnings = previews.Count(p => p.Warnings.Count > 0),
                errors = previews.Count(p => !p.CanApply),
                lines = previews,
            }));
        }

        try
        {
            var job = _bulkJobs.Start(items, request.Rate, source, request.Note);
            return Ok(ApiResponse<object>.Ok(BulkJobSnapshot(job)));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>GET /api/pricing/reprice/bulk — progress/result of the latest bulk job.</summary>
    [HttpGet("reprice/bulk")]
    public IActionResult GetBulkStatus()
    {
        var job = _bulkJobs.Current;
        return job is null
            ? NotFound(ApiResponse<object>.Fail("No bulk reprice job since service start."))
            : Ok(ApiResponse<object>.Ok(BulkJobSnapshot(job)));
    }

    /// <summary>POST /api/pricing/reprice/bulk/stop — gracefully stops the running job.</summary>
    [HttpPost("reprice/bulk/stop")]
    public IActionResult StopBulk()
        => _bulkJobs.Stop()
            ? Ok(ApiResponse<object>.Ok(BulkJobSnapshot(_bulkJobs.Current!)))
            : BadRequest(ApiResponse<object>.Fail("No bulk reprice job is currently running."));

    /// <summary>
    /// POST /api/pricing/reprice/bulk/upload — multipart Excel (.xlsx) with columns
    /// ItemCode / EurCost (header row optional; first two columns otherwise).
    /// Returns the parsed rows for preview — nothing is written.
    /// </summary>
    [HttpPost("reprice/bulk/upload")]
    public IActionResult UploadBulkExcel(IFormFile? file)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<object>.Fail("Upload an .xlsx file in the 'file' field."));
        try
        {
            var (rows, parseErrors) = ExcelRepriceParser.Parse(file.OpenReadStream());
            return Ok(ApiResponse<object>.Ok(new { rows, parse_errors = parseErrors }));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail($"Could not read the Excel file: {ex.Message}"));
        }
    }

    /// <summary>pricing_category name → the SAP group codes mapped to that band (scan of the group map).</summary>
    private List<int>? ResolveFilterGroups(int? sapGroup, string? pricingCategory)
    {
        if (sapGroup is int g) return new List<int> { g };
        if (string.IsNullOrWhiteSpace(pricingCategory)) return null;

        var groups = new List<int>();
        for (int code = 100; code <= 130; code++)
            if (string.Equals(_pricing.TryPricingBandForSapGroup(code), pricingCategory.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                groups.Add(code);
        return groups.Count > 0 ? groups : null;
    }

    private static object BulkJobSnapshot(BulkRepriceJob job) => new
    {
        job.JobId,
        Status = job.Status,
        job.StartedAt,
        job.FinishedAt,
        job.Source,
        job.RateOverride,
        job.TotalItems,
        job.Processed,
        job.Applied,
        job.Failed,
        job.Warned,
        job.Error,
        job.Failures,
        applied_sample = job.AppliedSample,
    };
}

/// <summary>PUT /api/pricing/rate body.</summary>
public record RateUpdateRequest(decimal Rate);

/// <summary>POST /api/pricing/reprice body.</summary>
public class RepriceRequest
{
    public string ItemCode { get; set; } = "";
    /// <summary>New EUR purchase price; omitted = reprice at the stored cost (rate change).</summary>
    public decimal? EurCost { get; set; }
    /// <summary>Rate override for this call; omitted = effective rate.</summary>
    public decimal? Rate { get; set; }
    public bool DryRun { get; set; } = true;
    public string? Note { get; set; }
}

public class BulkRepriceItemDto
{
    public string ItemCode { get; set; } = "";
    public decimal? EurCost { get; set; }
}

/// <summary>POST /api/pricing/reprice/bulk body.</summary>
public class BulkRepriceRequest
{
    public bool DryRun { get; set; } = true;
    public decimal? Rate { get; set; }
    public string? Note { get; set; }
    /// <summary>Explicit items (e.g. from the Excel upload).</summary>
    public List<BulkRepriceItemDto>? Items { get; set; }
    /// <summary>Filter mode: one SAP group code…</summary>
    public int? SapGroup { get; set; }
    /// <summary>…or a pricing band name (mapped to its SAP groups).</summary>
    public string? PricingCategory { get; set; }
}
