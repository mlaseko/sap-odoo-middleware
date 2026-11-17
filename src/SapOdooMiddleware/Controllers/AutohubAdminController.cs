using System.Linq;
using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Controllers;

/// <summary>Autohub operational/admin endpoints (API-key protected via middleware).</summary>
[ApiController]
[Route("api/autohub")]
public class AutohubAdminController : ControllerBase
{
    private readonly AutohubSapSetupVerifier _verifier;
    private readonly PartsSapReconciliationService _reconcile;
    private readonly ICatalogueCardRepairService _cardRepair;

    public AutohubAdminController(AutohubSapSetupVerifier verifier, PartsSapReconciliationService reconcile,
        ICatalogueCardRepairService cardRepair)
    {
        _verifier = verifier;
        _reconcile = reconcile;
        _cardRepair = cardRepair;
    }

    private static readonly string[] DefaultRepairBrands = { "VIKA", "DPA", "BORSEHUNG" };

    // "VIKA,DPA" → those brands; "all" → every brand; empty → VIKA, DPA, BORSEHUNG.
    private static IReadOnlyCollection<string> RepairBrands(string? brands) =>
        string.IsNullOrWhiteSpace(brands) ? DefaultRepairBrands
        : brands.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) ? Array.Empty<string>()
        : brands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// GET /api/autohub/catalogue-card-repair?brands=VIKA,DPA,BORSEHUNG
    /// Preview of the catalogue card repair: every SAP item whose catalogue card describes another part,
    /// and what the repair would do (link / create / duplicate / skip). Changes nothing.
    /// </summary>
    [HttpGet("catalogue-card-repair")]
    public async Task<IActionResult> CatalogueCardRepairPreview([FromQuery] string? brands, CancellationToken ct) =>
        Ok(await _cardRepair.PreviewAsync(RepairBrands(brands), ct));

    /// <summary>
    /// POST /api/autohub/catalogue-card-repair  body: { "brands": "VIKA,DPA,BORSEHUNG", "confirm": true }
    /// Applies the repair (links and creates; duplicates and skips are left). Returns the run id to undo it.
    /// </summary>
    [HttpPost("catalogue-card-repair")]
    public async Task<IActionResult> CatalogueCardRepairApply([FromBody] CardRepairRequest? body, CancellationToken ct)
    {
        if (body?.Confirm != true)
            return BadRequest(new { error = "Send \"confirm\": true to apply. GET the same URL first to preview." });
        var user = HttpContext.User?.Identity?.Name is { Length: > 0 } n ? n : "operator";
        return Ok(await _cardRepair.ApplyAsync(RepairBrands(body.Brands), user, ct));
    }

    /// <summary>POST /api/autohub/catalogue-card-repair/undo  body: { "run_id": "…" } — puts a repair run back.</summary>
    [HttpPost("catalogue-card-repair/undo")]
    public async Task<IActionResult> CatalogueCardRepairUndo([FromBody] CardRepairUndoRequest? body, CancellationToken ct)
    {
        if (body?.RunId is not { } runId || runId == Guid.Empty)
            return BadRequest(new { error = "run_id is required." });
        return Ok(new { run_id = runId, restored = await _cardRepair.UndoAsync(runId, ct) });
    }

    /// <summary>
    /// Pre-flight check that the Autohub SAP company has the master-data item-create assumes
    /// (price lists, item groups, OITM UDFs, VAT groups, UoM group 1). Read-only.
    /// </summary>
    [HttpGet("verify-sap-setup")]
    public async Task<IActionResult> VerifySapSetup(CancellationToken ct) =>
        Ok(await _verifier.VerifyAsync(ct));

    /// <summary>
    /// Re-create SAP items that exist in the Neon mirror but not in the Autohub SAP company, under their
    /// ORIGINAL codes, from the create-time data still stored on the source staging line. Idempotent — a
    /// code already present in SAP is skipped. Body (snake_case): { "item_codes": ["BM13010", ...] }.
    /// </summary>
    [HttpPost("reconcile-missing-sap-items")]
    public async Task<IActionResult> ReconcileMissingSapItems([FromBody] ReconcileMissingRequest body, CancellationToken ct)
    {
        if (body?.ItemCodes is not { Count: > 0 })
            return BadRequest(new { error = "item_codes is required (a non-empty list of SAP item codes)." });

        var results = await _reconcile.ReconcileAsync(body.ItemCodes, ct);
        return Ok(new
        {
            created = results.Count(r => r.Result == "created"),
            skipped = results.Count(r => r.Result == "skipped"),
            failed  = results.Count(r => r.Result == "failed"),
            results
        });
    }
}

public sealed record ReconcileMissingRequest(List<string> ItemCodes);
public sealed record CardRepairRequest(string? Brands, bool? Confirm);
public sealed record CardRepairUndoRequest(Guid? RunId);
