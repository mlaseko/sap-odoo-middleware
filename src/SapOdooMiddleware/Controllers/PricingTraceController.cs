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
    private readonly ILogger<PricingTraceController> _logger;

    public PricingTraceController(
        INeonProductRepository neon,
        IStagingDocumentLineRepository stagingLines,
        IPricingCalculator pricing,
        IOptions<PricingSettings> pricingSettings,
        ILogger<PricingTraceController> logger)
    {
        _neon = neon;
        _stagingLines = stagingLines;
        _pricing = pricing;
        _pricingSettings = pricingSettings.Value;
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
}
