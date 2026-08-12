using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Pricing;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Endpoints for SAP B1 inventory data.
/// </summary>
[ApiController]
[Route("api/inventory")]
public class InventoryController : ControllerBase
{
    private readonly ISapB1Service _sapService;
    private readonly IAutohubSapB1Service _autohubSapService;
    private readonly INeonProductRepository _neonRepo;
    private readonly IPricingCalculator _pricing;
    private readonly ILogger<InventoryController> _logger;

    public InventoryController(
        ISapB1Service sapService,
        IAutohubSapB1Service autohubSapService,
        INeonProductRepository neonRepo,
        IPricingCalculator pricing,
        ILogger<InventoryController> logger)
    {
        _sapService = sapService;
        _autohubSapService = autohubSapService;
        _neonRepo = neonRepo;
        _pricing = pricing;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/inventory/valuation/total
    /// Returns the total on-hand inventory value in TZS as computed by SAP B1.
    /// Requires the <c>X-Api-Key</c> header.
    /// </summary>
    /// <param name="asOfDate">
    /// Optional date (YYYY-MM-DD) to evaluate inventory as-of.
    /// Defaults to today's server date when omitted.
    /// </param>
    [HttpGet("valuation/total")]
    [ProducesResponseType(typeof(ApiResponse<InventoryValuationTotalResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<InventoryValuationTotalResponse>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetValuationTotal([FromQuery(Name = "as_of_date")] DateOnly? asOfDate = null)
    {
        var effectiveDate = asOfDate ?? DateOnly.FromDateTime(DateTime.Now);

        _logger.LogInformation(
            "Inventory valuation total requested for as_of_date={AsOfDate}",
            effectiveDate.ToString("yyyy-MM-dd"));

        try
        {
            decimal total = await _sapService.GetInventoryValuationTotalAsync(asOfDate);

            var response = new InventoryValuationTotalResponse
            {
                Currency = "TZS",
                AsOfDate = effectiveDate,
                TotalInventoryValueTzs = total
            };

            return Ok(ApiResponse<InventoryValuationTotalResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inventory valuation total failed for as_of_date={AsOfDate}.", effectiveDate.ToString("yyyy-MM-dd"));
            return StatusCode(500, ApiResponse<InventoryValuationTotalResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// GET /api/inventory/transactionhistory?item_code=BM10001
    /// Returns the full inventory transaction history for a single item from the
    /// Autohub SAP B1 company (MOLAS_Live_2021). Combines OINM-based inventory
    /// movements with open Purchase Orders, ordered by posting date.
    /// </summary>
    /// <param name="itemCode">SAP ItemCode (required).</param>
    /// <param name="fromDate">Optional start date filter (YYYY-MM-DD).</param>
    /// <param name="toDate">Optional end date filter (YYYY-MM-DD).</param>
    [HttpGet("transactionhistory")]
    [ProducesResponseType(typeof(ApiResponse<List<TransactionHistoryItem>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<List<TransactionHistoryItem>>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<List<TransactionHistoryItem>>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetTransactionHistory(
        [FromQuery(Name = "item_code")] string? itemCode,
        [FromQuery(Name = "from_date")] DateOnly? fromDate = null,
        [FromQuery(Name = "to_date")] DateOnly? toDate = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
            return BadRequest(ApiResponse<List<TransactionHistoryItem>>.Fail(
                "item_code query parameter is required."));

        _logger.LogInformation(
            "Transaction history requested: ItemCode={ItemCode}, From={From}, To={To}",
            itemCode, fromDate?.ToString("yyyy-MM-dd"), toDate?.ToString("yyyy-MM-dd"));

        try
        {
            var history = await _autohubSapService.GetTransactionHistoryAsync(
                itemCode.Trim(), fromDate, toDate, ct);

            return Ok(ApiResponse<List<TransactionHistoryItem>>.Ok(
                history,
                new Dictionary<string, object>
                {
                    ["item_code"] = itemCode.Trim(),
                    ["total_rows"] = history.Count,
                }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Transaction history failed for ItemCode={ItemCode}", itemCode);
            return StatusCode(500,
                ApiResponse<List<TransactionHistoryItem>>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// POST /api/inventory/backfill-pl04?dry_run=true
    /// Derives PL04 (Maasai) net prices from existing PL03 (Super Dealer) prices in Neon,
    /// then writes them to both Neon (PriceList=4) and SAP B1 (price-list index 3).
    /// <para>
    /// Pricing category is resolved from the <b>authoritative SAP item group code</b>
    /// (OITM.ItmsGrpCod via a single bulk Recordset query), with Neon's OdooCategoryName
    /// as a secondary fallback and "Service" as the final default.
    /// </para>
    /// When <paramref name="dryRun"/> is true the computation runs but no writes are made.
    /// Uses the Lubes SAP connection (not Autohub).
    /// </summary>
    [HttpPost("backfill-pl04")]
    [ProducesResponseType(typeof(ApiResponse<Pl04BackfillResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<Pl04BackfillResponse>), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> BackfillPl04(
        [FromQuery(Name = "dry_run")] bool dryRun = false,
        CancellationToken ct = default)
    {
        _logger.LogInformation("PL04 backfill started (dry_run={DryRun})", dryRun);

        try
        {
            // 1. Bulk-load authoritative SAP group codes (one Recordset query).
            var sapGroupCodes = await _sapService.GetItemGroupCodesAsync(ct);
            _logger.LogInformation(
                "Loaded {Count} SAP item group codes", sapGroupCodes.Count);

            // 2. Read PL03 prices from Neon.
            var pl03Items = await _neonRepo.GetItemsWithPl03Async(ct);
            _logger.LogInformation(
                "Found {Count} items with PL03 prices in Neon", pl03Items.Count);

            var results = new List<Pl04BackfillItemResult>();
            int successCount = 0, skipCount = 0, failCount = 0;

            foreach (var item in pl03Items)
            {
                try
                {
                    // 3. Resolve pricing category:
                    //    a) SAP group code (authoritative) → TryPricingBandForSapGroup
                    //    b) Neon OdooCategoryName           → ResolvePricingCategory
                    //    c) "Service" default
                    string? category = null;

                    if (sapGroupCodes.TryGetValue(item.ItemCode, out int sapGrp))
                        category = _pricing.TryPricingBandForSapGroup(sapGrp);

                    if (category == null && !string.IsNullOrWhiteSpace(item.OdooCategoryName))
                    {
                        try { category = _pricing.ResolvePricingCategory(item.OdooCategoryName); }
                        catch (InvalidOperationException) { /* unmapped Odoo category */ }
                    }

                    category ??= "Service";

                    // 4. Compute PL04 from PL03.
                    decimal pl04Net = _pricing.ComputeMaasaiNetFromPl03Net(
                        item.Pl03NetPrice, category);

                    if (pl04Net <= 0m)
                    {
                        results.Add(new Pl04BackfillItemResult(
                            item.ItemCode, item.Pl03NetPrice, 0m, category, "skipped",
                            "Computed PL04 price is zero"));
                        skipCount++;
                        continue;
                    }

                    // 5. Write to Neon + SAP.
                    if (!dryRun)
                    {
                        await _neonRepo.UpsertSinglePriceAsync(
                            item.ItemCode, 4, pl04Net, ct);

                        await _sapService.SetPriceListPriceAsync(
                            item.ItemCode, 3, pl04Net, ct);
                    }

                    results.Add(new Pl04BackfillItemResult(
                        item.ItemCode, item.Pl03NetPrice, pl04Net, category,
                        dryRun ? "dry_run" : "success", null));
                    successCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "PL04 backfill failed for ItemCode={ItemCode}", item.ItemCode);
                    results.Add(new Pl04BackfillItemResult(
                        item.ItemCode, item.Pl03NetPrice, 0m, null, "failed", ex.Message));
                    failCount++;
                }
            }

            var response = new Pl04BackfillResponse
            {
                DryRun = dryRun,
                TotalItems = pl03Items.Count,
                Succeeded = successCount,
                Skipped = skipCount,
                Failed = failCount,
                Items = results
            };

            _logger.LogInformation(
                "PL04 backfill complete: total={Total}, success={Success}, skip={Skip}, fail={Fail}, dry_run={DryRun}",
                pl03Items.Count, successCount, skipCount, failCount, dryRun);

            return Ok(ApiResponse<Pl04BackfillResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PL04 backfill failed");
            return StatusCode(500, ApiResponse<Pl04BackfillResponse>.Fail(ex.Message));
        }
    }
}

/// <summary>Result of a PL04 backfill run.</summary>
public class Pl04BackfillResponse
{
    public bool DryRun { get; set; }
    public int TotalItems { get; set; }
    public int Succeeded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<Pl04BackfillItemResult> Items { get; set; } = new();
}

/// <summary>Per-item result in the backfill response.</summary>
public record Pl04BackfillItemResult(
    string ItemCode,
    decimal Pl03NetPrice,
    decimal Pl04NetPrice,
    string? PricingCategory,
    string Status,
    string? Error);
