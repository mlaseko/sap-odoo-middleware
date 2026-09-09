using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Pricing;

namespace SapOdooMiddleware.Pages.Pricing;

/// <summary>
/// Ratio editor: pick a category + cost band from dropdowns (values pre-filled from
/// the live calculator), edit the three tier ratios or the Maasai ratio, SIMULATE
/// against any cost to see the resulting prices, then Approve &amp; Save — the
/// calculator updates immediately and the override persists in Neon.
/// </summary>
public class RatiosModel : PageModel
{
    private readonly IPricingCalculator _calc;
    private readonly ILubesPricingRepository _repo;
    private readonly ILogger<RatiosModel> _logger;

    public RatiosModel(IPricingCalculator calc, ILubesPricingRepository repo, ILogger<RatiosModel> logger)
    {
        _calc = calc;
        _repo = repo;
        _logger = logger;
    }

    public PricingRatioSnapshot Snapshot { get; private set; } = null!;
    public EffectiveRate Rate { get; private set; } = new(0m, "", null);
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    // Sticky form state
    public string? SelCategory { get; private set; }
    public string? SelBand { get; private set; }
    public decimal? SpIn { get; private set; }
    public decimal? DealerIn { get; private set; }
    public decimal? RetailIn { get; private set; }
    public int? SelMaasaiBand { get; private set; }
    public decimal? MaasaiIn { get; private set; }
    public decimal? SimCost { get; private set; }
    public string SimUnit { get; private set; } = "eur";
    public PricingTrace? SimResult { get; private set; }
    public PricingTrace? SimBaseline { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        Snapshot = _calc.GetRatioSnapshot();
        Rate = await _repo.GetEffectiveRateAsync(ct);
    }

    public async Task<IActionResult> OnPostSimulateAsync(
        string category, string band, decimal sp, decimal dealer, decimal retail,
        int maasaiBand, decimal maasaiRatio,
        decimal simCost, string simUnit, CancellationToken ct)
    {
        await LoadAsync(ct);
        (SelCategory, SelBand, SpIn, DealerIn, RetailIn) = (category, band, sp, dealer, retail);
        (SelMaasaiBand, MaasaiIn, SimCost, SimUnit) = (maasaiBand, maasaiRatio, simCost, simUnit);

        if (!ValidateRatios(sp, dealer, retail, maasaiRatio)) return Page();
        if (simCost <= 0m) { Error = "Enter a cost to simulate."; return Page(); }

        var cif = simUnit == "eur" ? simCost * Rate.Rate : simCost;
        try
        {
            SimResult = _calc.Simulate(cif, category,
                new BandRatioOverride(category, band, sp, dealer, retail),
                new MaasaiRatioOverride(category, maasaiBand, maasaiRatio));
            SimBaseline = _calc.ComputeNetPricesWithTrace(cif, category);
            Message = "Simulation only — the calculator is unchanged until you Approve & Save.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveBandAsync(
        string category, string band, decimal sp, decimal dealer, decimal retail,
        string? note, CancellationToken ct)
    {
        await LoadAsync(ct);
        (SelCategory, SelBand, SpIn, DealerIn, RetailIn) = (category, band, sp, dealer, retail);
        if (!ValidateRatios(sp, dealer, retail, null)) return Page();

        await _repo.UpsertBandRatioOverrideAsync(
            new BandRatioOverride(category, band, sp, dealer, retail), note, ct);
        await ReloadCalculatorAsync(ct);
        _logger.LogInformation(
            "Band ratios saved: {Category} / {Band} → sp={Sp} d={D} r={R}", category, band, sp, dealer, retail);
        Message = $"✅ Saved and live: {category} / {band} → sp={sp}, dealer={dealer}, retail={retail}. " +
                  "All future pricing (provisioning + repricing) uses these ratios.";
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveMaasaiAsync(
        string category, int maasaiBand, decimal maasaiRatio, string? note, CancellationToken ct)
    {
        await LoadAsync(ct);
        (SelCategory, SelMaasaiBand, MaasaiIn) = (category, maasaiBand, maasaiRatio);
        if (maasaiRatio is <= 0m or >= 2m) { Error = "Maasai ratio must be between 0 and 2."; return Page(); }

        await _repo.UpsertMaasaiRatioOverrideAsync(
            new MaasaiRatioOverride(category, maasaiBand, maasaiRatio), note, ct);
        await ReloadCalculatorAsync(ct);
        Message = $"✅ Saved and live: {category} Maasai band {(char)('A' + maasaiBand)} → {maasaiRatio}.";
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostResetBandAsync(string category, string band, CancellationToken ct)
    {
        await _repo.DeleteBandRatioOverrideAsync(category, band, ct);
        await ReloadCalculatorAsync(ct);
        Message = $"{category} / {band} reverted to the built-in default ratios.";
        await LoadAsync(ct);
        (SelCategory, SelBand) = (category, band);
        return Page();
    }

    public async Task<IActionResult> OnPostResetMaasaiAsync(string category, int maasaiBand, CancellationToken ct)
    {
        await _repo.DeleteMaasaiRatioOverrideAsync(category, maasaiBand, ct);
        await ReloadCalculatorAsync(ct);
        Message = $"{category} Maasai band {(char)('A' + maasaiBand)} reverted to the built-in default.";
        await LoadAsync(ct);
        (SelCategory, SelMaasaiBand) = (category, maasaiBand);
        return Page();
    }

    private async Task ReloadCalculatorAsync(CancellationToken ct)
    {
        var (band, maasai) = await _repo.GetRatioOverridesAsync(ct);
        _calc.ApplyRatioOverrides(band, maasai);
    }

    private bool ValidateRatios(decimal sp, decimal dealer, decimal retail, decimal? maasai)
    {
        if (sp <= 0m || dealer <= 0m || retail <= 0m || sp >= 2m || dealer >= 2m || retail >= 2m)
        {
            Error = "Ratios must be between 0 and 2 (e.g. 0.5927).";
            return false;
        }
        // Since price = cost / ratio, a bigger ratio means a LOWER price. Expected
        // ordering sp > dealer > retail keeps SP < Dealer < Retail prices.
        if (!(sp > dealer && dealer > retail))
            Message = "⚠ Note: expected sp > dealer > retail (so SP price < Dealer < Retail). " +
                      "Saving anyway is allowed — the calculator will force the price ordering.";
        if (maasai is not null && maasai is <= 0m or >= 2m)
        {
            Error = "Maasai ratio must be between 0 and 2.";
            return false;
        }
        return true;
    }
}
