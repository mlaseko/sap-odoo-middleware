using System.Text.Json;
using System.Text.Json.Serialization;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Per-line provisioning outcome. Status ∈ {created, failed, needs_confirmation, needs_manufacturer,
/// prefix_exhausted}. The last two are HOLDS — not failures — awaiting an operator decision (assign the
/// marque / extend the SKU range) before the line can be created.
/// </summary>
public sealed record PartsProvisioningOutcome(string Status, string? ItemCode, string? Error);

/// <summary>
/// Reviewer price decision for one line (the price review gate). At most one of
/// UnitPriceOverride (corrected invoice unit price, DOCUMENT currency, replaces the
/// extracted price before forex) and CifOverride (corrected landed cost, TZS pre-markup,
/// skips forex entirely). Pl03Override is applied to PL03 verbatim, PL05 re-derived.
/// Every override is recorded in pricing_overrides.
/// </summary>
public sealed record PartsLineOverride(
    decimal? UnitPriceOverride, decimal? CifOverride, decimal? Pl03Override,
    string? OverrideReason, string? RequestedBy)
{
    public bool HasAny => UnitPriceOverride is not null || CifOverride is not null || Pl03Override is not null;
}

public interface IPartsItemProvisioningService
{
    Task<PartsProvisioningOutcome> ProvisionAsync(
        PartsProvisioningLine line, string? currency, CancellationToken ct,
        PartsLineOverride? priceOverride = null);
}

/// <summary>
/// Turns one reviewed 'create_new' parts line into a real SAP item (D-series pipeline §10):
///   filter OEMs → enrich (DGX, idempotent) → forex → price → allocate SKU → write OITM → bridge to
///   the Neon mirror so auto-match finds it next time. SAP is the system of record; the line is
///   only marked 'created' once the OITM write succeeds (the Neon bridge is best-effort and can be
///   re-published), which prevents a retry from minting a duplicate SAP item under a fresh SKU.
/// Persists its own outcome (RecordCreated / RecordCreateFailed); the bulk caller just tallies.
/// </summary>
public sealed class PartsItemProvisioningService : IPartsItemProvisioningService
{
    private readonly IEnrichmentService _enrichment;
    private readonly IForexConversionService _forex;
    private readonly IPricingCalculationService _pricing;
    private readonly ISkuGenerationService _sku;
    private readonly IOemFilterService _filter;
    // Autohub items are created in the Autohub company (Companies:Autohub:SapB1), NOT the default Lubes
    // company — so this is the Autohub-bound SAP connection, not the shared ISapB1Service.
    private readonly IAutohubSapB1Service _sap;
    private readonly INeonBridgeService _bridge;
    private readonly IPartsReviewRepository _review;
    private readonly IPricingOverrideRepository _overrides;
    private readonly ILogger<PartsItemProvisioningService> _logger;

    public PartsItemProvisioningService(
        IEnrichmentService enrichment, IForexConversionService forex, IPricingCalculationService pricing,
        ISkuGenerationService sku, IOemFilterService filter, IAutohubSapB1Service sap, INeonBridgeService bridge,
        IPartsReviewRepository review, IPricingOverrideRepository overrides,
        ILogger<PartsItemProvisioningService> logger)
    {
        _enrichment = enrichment;
        _forex = forex;
        _pricing = pricing;
        _sku = sku;
        _filter = filter;
        _sap = sap;
        _bridge = bridge;
        _review = review;
        _overrides = overrides;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions EnrichmentJson = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task<PartsProvisioningOutcome> ProvisionAsync(
        PartsProvisioningLine line, string? currency, CancellationToken ct,
        PartsLineOverride? priceOverride = null)
    {
        var article = line.SupplierArticleNumber?.Trim();
        if (string.IsNullOrWhiteSpace(article))
            return await Fail(line.Id, "Line has no supplier article number.", ct);

        // Cost inputs. A reviewer's corrected invoice price replaces the extracted one
        // (used as-is — the reviewer sees the final number); a CIF override skips forex
        // entirely, so neither the unit price nor the currency is required then.
        var unitPrice = priceOverride?.UnitPriceOverride ?? line.UnitPriceForeign;
        if (priceOverride?.CifOverride is null)
        {
            if (unitPrice is not > 0m)
                return await Fail(line.Id, "Line has no positive unit price.", ct);
            if (string.IsNullOrWhiteSpace(currency))
                return await Fail(line.Id, "Document currency is unknown; cannot convert cost.", ct);
        }

        // Idempotency guard: if a SAP item already exists for this (supplier, article), match it instead of
        // minting a duplicate. Protects against a re-upload / re-run of the same invoice, and against two
        // lines for the same part in one batch (the first mirrors to oitm, the second matches it).
        // The Neon code is only trusted when it ALSO exists in SAP — a mirror row whose SAP item was never
        // durably created (old-build bug) or was later removed must NOT short-circuit to a phantom match;
        // in that case fall through and create a real item.
        if (await FindExistingItemCodeAsync(article!, line.Brand, ct) is { } existingCode)
        {
            if (await _sap.ItemExistsAsync(existingCode))
            {
                _logger.LogInformation(
                    "Line {LineId}: SAP item {Code} already exists for {Article}/{Supplier}; matching instead of creating a duplicate.",
                    line.Id, existingCode, article, line.Brand);
                await _review.SetReviewStatusAsync(line.Id, "matched", existingCode, ct);
                return new PartsProvisioningOutcome("created", existingCode, null);
            }
            _logger.LogWarning(
                "Line {LineId}: Neon mirror has {Code} for {Article}/{Supplier} but it is NOT in SAP (phantom); creating a real item instead of matching.",
                line.Id, existingCode, article, line.Brand);
        }

        var filtered = _filter.Filter(line.OemNumbers, article, line.Brand).CleanOems;

        // Prefer the enrichment persisted at review time (what the operator saw/confirmed); only
        // re-call DGX if the line was never enriched (e.g. created straight from a quick match).
        // The re-fetch is idempotent — DGX returns the same oitm row for the same article+brand.
        EnrichmentResponse enr;
        if (!string.IsNullOrWhiteSpace(line.EnrichmentPayloadJson))
        {
            try
            {
                enr = JsonSerializer.Deserialize<EnrichmentResponse>(line.EnrichmentPayloadJson, EnrichmentJson)
                      ?? throw new InvalidOperationException("empty payload");
            }
            catch (Exception ex)
            {
                return await Fail(line.Id, $"Stored enrichment could not be read: {ex.Message}", ct);
            }
        }
        else
        {
            try
            {
                enr = await _enrichment.EnrichLineAsync(
                    new EnrichmentInput(article, filtered, line.Brand, line.Description, null), ct);
            }
            catch (Exception ex)
            {
                return await Fail(line.Id, $"Enrichment failed: {ex.Message}", ct);
            }
        }

        if (enr.ItemData is null)
            return await Fail(line.Id, "Enrichment returned no item data.", ct);
        // Only a GENUINE cross-supplier borrow (another supplier's data applied to our item) needs operator
        // sign-off before creating — same-supplier / own-data enrichment creates straight through. DGX's
        // blanket confirmation_required fires on nearly everything, so it is NOT the gate (it blocked bulk
        // creation for every borrowed/unmatched line). The MatchStrategy already records the cross-supplier
        // decision the router made.
        if (EnrichmentStrategies.IsCrossSupplierStrategy(line.MatchStrategy) && !line.EnrichmentConfirmed)
            return new PartsProvisioningOutcome("needs_confirmation", null,
                "Cross-supplier borrowed enrichment requires operator confirmation before creation.");

        var data = enr.ItemData;
        if (data.SuggestedItmsGrpCod is not { } groupCode)
            return await Fail(line.Id, "Enrichment did not return a SAP item group (suggested_itms_grp_cod).", ct);
        // The SKU prefix IS the manufacturer/marque code (BM, MB, VAG, …) and DGX is its sole authority. If
        // DGX could not resolve the marque, hold the line for an operator to assign it — NEVER mint a generic
        // 'GEN' item (that produced mis-prefixed items + duplicates).
        //
        // "Unresolved" arrives two ways: a null/blank prefix, OR the literal "GEN" sent through the normal
        // path while DGX runs MRES_SHADOW=1 (legacy behaviour preserved by design). BOTH must hold — a "GEN"
        // string is a real value that would otherwise sail past a null-only check and be minted. Operator
        // candidates ride on the response's manufacturer_resolution block (captured for the UI in Part 2).
        if (IsUnresolvedPrefix(data.SuggestedSkuPrefix))
            return await Held(line.Id, "needs_manufacturer",
                "Manufacturer could not be resolved automatically — assign the marque so a SAP item code can be generated.", ct);
        var prefix = data.SuggestedSkuPrefix!.Trim();

        // Forex → landed cost (TZS), and the rate we used (for audit). The extracted
        // invoice price is gross: a line discount (DiscountPct) reduces the real cost, so
        // apply it before forex. A reviewer's corrected price is taken as-is (the gate
        // shows the reviewer the final figure), and a CIF override skips this path.
        decimal costTzs;
        decimal rate = 0m;
        if (priceOverride?.CifOverride is { } cifOverride)
        {
            costTzs = cifOverride;
            if (unitPrice is > 0m)
                rate = Math.Round(costTzs / unitPrice.Value, 6, MidpointRounding.AwayFromZero);
        }
        else
        {
            var effectiveUnit = unitPrice!.Value;
            if (priceOverride?.UnitPriceOverride is null
                && line.DiscountPct is > 0m and < 100m)
            {
                effectiveUnit = Math.Round(effectiveUnit * (1m - line.DiscountPct.Value / 100m), 6,
                    MidpointRounding.AwayFromZero);
            }
            try
            {
                costTzs = await ConvertToTzsWithRetryAsync(effectiveUnit, currency!, ct);
            }
            catch (Exception ex)
            {
                return await Fail(line.Id, $"Forex conversion failed after retries: {ex.Message}", ct);
            }
            rate = Math.Round(costTzs / effectiveUnit, 6, MidpointRounding.AwayFromZero);
        }

        PricingQuote quote;
        try
        {
            quote = await _pricing.CalculateDetailedAsync(costTzs, line.Brand ?? "", ct);
        }
        catch (Exception ex)
        {
            return await Fail(line.Id, $"Pricing failed: {ex.Message}", ct);
        }

        // Reviewer PL03 override: applied verbatim (no re-rounding); PL05 re-derived by
        // the engine's wholesale rule from the applied cost/retail pair.
        var retail = priceOverride?.Pl03Override ?? quote.Retail;
        var wholesale = priceOverride?.Pl03Override is not null
            ? await _pricing.DeriveWholesaleAsync(quote.Cost, retail, ct)
            : quote.Wholesale;
        var prices = new PricingResult(quote.Cost, retail, wholesale, quote.Ratio);
        if (prices.Cost <= 0m)
            return await Fail(line.Id, "Computed price-list 01 (cost) is zero — check the forex rate and pricing config before creating the SAP item.", ct);

        // Allocate the final ItemCode. NOTE: the counter is atomic but burns a number even if the
        // SAP write below fails — gaps in SAP item codes are acceptable; we never reuse/duplicate.
        // A counter at its MaxAllowed ceiling is a hold (operator extends the range), never a silent overrun.
        string itemCode;
        try
        {
            itemCode = await _sku.GenerateAsync(prefix, ct);
        }
        catch (SkuCounterExhaustedException ex)
        {
            return await Held(line.Id, "prefix_exhausted", ex.Message, ct);
        }
        // ItemName carries the OEM cross-references: the line's invoice OEM(s) PLUS the donor's OEM
        // cross-references — reference_type='oem' ONLY, never aftermarket/IAM equivalents — up to five,
        // then the supplier article. The invoice usually lists a single OEM, so without these the item
        // would show just one.
        var donorOems = enr.NeonOitmId is { } oemDonorId
            ? await _bridge.GetOemCrossReferencesAsync(oemDonorId, ct)
            : (IReadOnlyList<string>)Array.Empty<string>();
        // Germax parts carry a second in-house number; append it after the primary when present.
        var altArticles = await _bridge.GetGermaxAlternateArticleNumbersAsync(article!, ct);
        var itemName = BuildItemName(MergeOems(filtered, donorOems), article!, altArticles);

        var sapReq = new SapAutohubItemRequest(
            ItemCode: itemCode,
            ItemName: itemName,
            ItemsGroupCode: groupCode,
            CostPrice: prices.Cost,
            RetailPrice: prices.Retail,
            WholesalePrice: prices.Wholesale,
            ArticleNumber: article!,
            PartName: data.PrimaryDescription ?? line.Description,
            Manufacturer: line.Brand);

        try
        {
            await _sap.CreateAutohubItemAsync(sapReq);
        }
        catch (Exception ex)
        {
            return await Fail(line.Id, $"SAP item write failed: {ex.Message}", ct);
        }

        // Audit every reviewer override next to the formula's answer (pricing_overrides)
        // — recorded only after the SAP commit, best effort (never fails the line).
        if (priceOverride is { HasAny: true })
        {
            var costCorrected = priceOverride.UnitPriceOverride is not null || priceOverride.CifOverride is not null;
            var source = (costCorrected, priceOverride.Pl03Override is not null) switch
            {
                (true, true) => "cif+pl03_override",
                (true, false) => "cif_override",
                _ => "pl03_override",
            };
            try
            {
                await _overrides.RecordAsync(new PricingOverrideRecord(
                    ItemCode: itemCode, Brand: line.Brand, Cif: costTzs,
                    FormulaPl01: quote.Cost, FormulaPl03: quote.Retail, FormulaPl05: quote.Wholesale,
                    AppliedPl01: prices.Cost, AppliedPl03: prices.Retail, AppliedPl05: prices.Wholesale,
                    Source: source, Reason: priceOverride.OverrideReason,
                    RequestedBy: priceOverride.RequestedBy), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "pricing_overrides insert failed for {ItemCode} (line {LineId}) — item created; record manually.",
                    itemCode, line.Id);
            }
        }

        // Bridge: stamp the SAP ItemCode onto the pre-enriched parts_catalog row so auto-match finds
        // it. Best-effort: the item already exists in SAP, so a bridge failure must NOT mark the line
        // failed (that would mint a duplicate on retry) — log it; an admin reconcile can re-link by id.
        if (enr.NeonOitmId is { } neonOitmId)
        {
            try
            {
                if (EnrichmentStrategies.IsCrossSupplierStrategy(line.MatchStrategy))
                {
                    // Cross-supplier: never write our code to the donor (different supplier). Mint an
                    // own-identity oitm row for the new SAP item and repoint the line at it, so future
                    // invoices for this (brand, article) auto-match instead of minting another duplicate.
                    var newId = await _bridge.CreateOwnIdentityRowAsync(
                        neonOitmId, itemCode, EnrichmentStrategies.ResolveOwnIdentitySource(line.MatchStrategy),
                        line.SupplierArticleNumber, line.Brand, ct);
                    if (newId is { } nid)
                        await _review.UpdateNeonOitmIdAsync(line.Id, nid, ct);
                    else
                        _logger.LogWarning(
                            "SAP item {ItemCode} created but donor oitm {OitmId} missing; own-identity row not minted.",
                            itemCode, neonOitmId);
                }
                else
                {
                    var link = await _bridge.LinkAsync(neonOitmId, itemCode, ct);
                    if (link.Status == NeonBridgeLinkStatus.BlockedByExisting)
                        _logger.LogError(
                            "SAP item {ItemCode} created but oitm id {OitmId} was already linked to '{Existing}' — likely a duplicate SAP item; reconcile.",
                            itemCode, neonOitmId, link.ExistingItemCode);
                    else if (link.Status == NeonBridgeLinkStatus.NotFound)
                        _logger.LogWarning(
                            "SAP item {ItemCode} created but oitm id {OitmId} not found; cannot link the Neon mirror.",
                            itemCode, neonOitmId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SAP item {ItemCode} created but Neon bridge link (oitm id {OitmId}) failed; reconcile required.",
                    itemCode, neonOitmId);
            }
        }
        else
        {
            // No donor oitm to stamp (e.g. germax_local / fresh enrichment with no parts_catalog match):
            // mint a fresh own-identity Neon row so the new SAP item lands in oitm too and future invoices
            // for this (supplier, article) auto-match. Best-effort — the SAP
            // item already exists, so a mirror failure must NOT fail the line (a retry would mint a
            // duplicate). The line OEMs (reference_type='oem' equivalents) seed the cross-references.
            try
            {
                var supplier = string.IsNullOrWhiteSpace(line.Brand) ? null : line.Brand;
                await _bridge.CreateFreshRowAsync(itemCode, article!, supplier, filtered, "enrichment_no_donor",
                    data.PrimaryDescription ?? line.Description, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SAP item {ItemCode} created but the no-donor Neon mirror insert failed; reconcile required.",
                    itemCode);
            }
        }

        await _review.RecordCreatedAsync(line.Id, itemCode, prices.Cost, prices.Retail, prices.Wholesale, rate, ct);
        return new PartsProvisioningOutcome("created", itemCode, null);
    }

    /// <summary>
    /// D5 ItemName: up to five OEMs, then the supplier article, then any alternate Germax article numbers
    /// that still fit — joined by '/'. The OEM chain + primary article are ALWAYS kept (hard-capped at 200);
    /// alternates are appended only while the whole name stays within the cap, so a long OEM chain never
    /// pushes out the primary. No alternates ⇒ identical to before.
    /// </summary>
    internal static string BuildItemName(IReadOnlyList<string> oems, string article, IReadOnlyList<string>? alternateArticles = null)
    {
        const int maxLen = 200;
        var parts = oems.Take(5).ToList();
        parts.Add(article);
        var name = string.Join("/", parts);
        if (name.Length > maxLen) return name[..maxLen];   // OEM chain + primary already at the cap

        foreach (var alt in alternateArticles ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(alt)) continue;
            var candidate = name + "/" + alt.Trim();
            if (candidate.Length > maxLen) break;          // append alternates only while they fit
            name = candidate;
        }
        return name;
    }

    /// <summary>
    /// The line's invoice OEM(s) first, then the enrichment cross-reference OEMs (filtered_oems),
    /// de-duplicated (case-insensitive, order preserved). <see cref="BuildItemName"/> keeps up to five.
    /// </summary>
    private static List<string> MergeOems(IReadOnlyList<string> lineOems, IReadOnlyList<string>? enrichedOems)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();
        foreach (var o in lineOems.Concat(enrichedOems ?? Enumerable.Empty<string>()))
        {
            var t = o?.Trim();
            if (!string.IsNullOrEmpty(t) && seen.Add(t)) merged.Add(t);
        }
        return merged;
    }

    /// <summary>
    /// Forex conversion with a SHORT exponential backoff (1s → 2s → 4s, 3 attempts) to ride out a transient
    /// blip reading the forex_rate table. Deliberately short — NOT the minutes-to-hours schedule used by the
    /// background workers — because this runs inline inside Bulk Create's per-item timeout. A deterministic
    /// failure (e.g. no rate row for the currency) just exhausts the attempts and surfaces the same error.
    /// Cancellation (the per-item timeout / host shutdown) is never retried.
    /// </summary>
    private async Task<decimal> ConvertToTzsWithRetryAsync(decimal amount, string currency, CancellationToken ct)
    {
        const int maxAttempts = 3;
        var delay = TimeSpan.FromSeconds(1);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await _forex.ConvertToTzsAsync(amount, currency, DateTime.UtcNow, ct);
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Forex conversion attempt {Attempt}/{Max} failed for {Currency}; retrying in {Delay}s.",
                    attempt, maxAttempts, currency, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromTicks(delay.Ticks * 2);
            }
        }
    }

    /// <summary>
    /// The SAP ItemCode of an already-created item for this (supplier, article), or null if none exists.
    /// The duplicate-create guard: an own-identity oitm row carries a populated item_code only once its SAP
    /// item was created, so this fires on a re-run but not on the first create (item_code still NULL then).
    /// </summary>
    private async Task<string?> FindExistingItemCodeAsync(string article, string? brand, CancellationToken ct) =>
        await _bridge.FindItemCodeByArticleSupplierAsync(
            article, string.IsNullOrWhiteSpace(brand) ? null : brand, ct);

    /// <summary>
    /// A SKU prefix that must NOT be minted: blank, or the legacy generic <c>GEN</c> marker DGX still emits
    /// for a GEN-class line while <c>MRES_SHADOW=1</c>. Either means "marque unresolved" — the line is held,
    /// never coded. Case- and whitespace-insensitive so "gen"/" GEN " are caught too.
    /// </summary>
    internal static bool IsUnresolvedPrefix(string? prefix) =>
        string.IsNullOrWhiteSpace(prefix) || string.Equals(prefix.Trim(), "GEN", StringComparison.OrdinalIgnoreCase);

    private async Task<PartsProvisioningOutcome> Fail(Guid lineId, string error, CancellationToken ct)
    {
        await _review.RecordCreateFailedAsync(lineId, error, ct);
        return new PartsProvisioningOutcome("failed", null, error);
    }

    /// <summary>
    /// Park a line in a hold state (<c>needs_manufacturer</c> / <c>prefix_exhausted</c>) with an
    /// operator-facing reason, WITHOUT recording it as a failure. A hold means "waiting on a human
    /// decision", not "errored": it is excluded from the bulk-create retry set (ListCreateNewAsync) and
    /// shown with its own review pill so the operator can resolve it and re-run.
    /// </summary>
    private async Task<PartsProvisioningOutcome> Held(Guid lineId, string status, string reason, CancellationToken ct)
    {
        await _review.RecordHeldAsync(lineId, status, reason, ct);
        return new PartsProvisioningOutcome(status, null, reason);
    }
}
