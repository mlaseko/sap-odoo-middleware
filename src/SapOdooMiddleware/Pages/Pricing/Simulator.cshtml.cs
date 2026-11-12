using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Pricing;

namespace SapOdooMiddleware.Pages.Pricing;

/// <summary>
/// Price simulator for items that are not (yet) in SAP or Odoo: same calculator as provisioning and
/// /pricing (ratios, top-band markups, high-cost cap, Maasai floor). Items are kept in a hidden list
/// on the page only — nothing is saved and nothing is posted to SAP, Neon or Odoo.
/// </summary>
public class SimulatorModel : PageModel
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ILubesPriceSimulator _sim;
    private readonly ILubesPricingRepository _pricingRepo;

    public SimulatorModel(ILubesPriceSimulator sim, ILubesPricingRepository pricingRepo)
    {
        _sim = sim;
        _pricingRepo = pricingRepo;
    }

    /// <summary>LIQUI MOLY SAP item groups with a pricing band (Nov 2026 OITB).</summary>
    public static readonly (int Code, string Name)[] SapGroups =
    {
        (105, "Engine Oils"), (106, "Commercial vehicles (heavy-duty engine oils)"),
        (107, "Gear Oils/ATF/Greases"), (109, "Additives"), (111, "Vehicle Care"),
        (112, "Workshop Pro-Line"), (117, "Greases"), (118, "Oils (Industrial/Other Fluids)"),
        (119, "Pastes"), (120, "Service Products"), (104, "Repair aids/service products"),
        (121, "Adhesives & Sealants"),
    };

    public EffectiveRate Rate { get; private set; } = new(0m, "", null);
    public decimal? RateOverride { get; private set; }
    public List<SimulationInput> Inputs { get; private set; } = new();
    public List<SimulationResult> Results { get; private set; } = new();
    public string InputsJson => JsonSerializer.Serialize(Inputs, JsonOpts);
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public List<string> ParseErrors { get; private set; } = new();

    /// <summary>The single-item form (sticky between posts).</summary>
    public sealed class ItemForm
    {
        public string? Reference { get; set; }
        public string? Description { get; set; }
        public decimal Cost { get; set; }
        public string Currency { get; set; } = "EUR";
        public string? Band { get; set; }
        public int? SapGroup { get; set; }
        public string? OdooCategory { get; set; }
        public string? Benchmark { get; set; }
    }

    [BindProperty] public ItemForm Form { get; set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
        => Rate = await _pricingRepo.GetEffectiveRateAsync(ct);

    public async Task<IActionResult> OnPostAddAsync(string? inputsJson, decimal? rateOverride, CancellationToken ct)
    {
        Inputs = Load(inputsJson);
        if (Form.Cost <= 0m)
            Error = "Enter a cost greater than zero.";
        else if (string.IsNullOrWhiteSpace(Form.Band) && Form.SapGroup is null && string.IsNullOrWhiteSpace(Form.OdooCategory))
            Error = "Pick a pricing band or a SAP group.";
        else if (Inputs.Count >= LubesPriceSimulator.MaxItems)
            Error = $"The list is full ({LubesPriceSimulator.MaxItems} items). Download it and start a new one.";
        else
            Inputs.Add(new SimulationInput(
                Trim(Form.Reference), Trim(Form.Description), Form.Cost, Form.Currency,
                Trim(Form.Band), Form.SapGroup, Trim(Form.OdooCategory), Trim(Form.Benchmark)));
        await RunAsync(rateOverride, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRecalculateAsync(string? inputsJson, decimal? rateOverride, CancellationToken ct)
    {
        Inputs = Load(inputsJson);
        await RunAsync(rateOverride, ct);
        if (Error is null) Message = "Recalculated with the current rate and ratios. Nothing was saved or posted.";
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string? inputsJson, int index, decimal? rateOverride, CancellationToken ct)
    {
        Inputs = Load(inputsJson);
        if (index >= 0 && index < Inputs.Count) Inputs.RemoveAt(index);
        await RunAsync(rateOverride, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(
        IFormFile? file, string? inputsJson, bool append, decimal? rateOverride, CancellationToken ct)
    {
        Inputs = append ? Load(inputsJson) : new();
        if (file is null || file.Length == 0)
        {
            Error = "Choose an .xlsx file first.";
        }
        else
        {
            try
            {
                var (rows, errors) = SimulatorExcel.Parse(file.OpenReadStream());
                ParseErrors = errors;
                Inputs.AddRange(rows);
                if (Inputs.Count > LubesPriceSimulator.MaxItems)
                {
                    Error = $"{Inputs.Count} items exceed the {LubesPriceSimulator.MaxItems}-item cap — split the file.";
                    Inputs = Inputs.Take(LubesPriceSimulator.MaxItems).ToList();
                }
                else
                {
                    Message = $"{rows.Count} item(s) read from {file.FileName}. Nothing was saved or posted.";
                }
            }
            catch (Exception ex)
            {
                Error = $"Could not read the Excel file: {ex.Message}";
            }
        }
        await RunAsync(rateOverride, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDownloadAsync(string? inputsJson, decimal? rateOverride, CancellationToken ct)
    {
        Inputs = Load(inputsJson);
        if (Inputs.Count == 0)
        {
            Error = "Nothing to download — add items first.";
            Rate = await _pricingRepo.GetEffectiveRateAsync(ct);
            return Page();
        }
        var results = await _sim.SimulateAsync(Inputs, rateOverride, ct);
        return File(SimulatorExcel.Write(results),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"lubes-price-simulation-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx");
    }

    public IActionResult OnGetTemplate()
        => File(SimulatorExcel.Template(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "lubes-price-simulator-template.xlsx");

    private async Task RunAsync(decimal? rateOverride, CancellationToken ct)
    {
        Rate = await _pricingRepo.GetEffectiveRateAsync(ct);
        RateOverride = rateOverride is > 0m ? rateOverride : null;
        if (Inputs.Count == 0) return;
        try
        {
            Results = await _sim.SimulateAsync(Inputs, RateOverride, ct);
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }
    }

    private static List<SimulationInput> Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<SimulationInput>>(json, JsonOpts) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
