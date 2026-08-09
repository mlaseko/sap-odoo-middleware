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

    public AutohubAdminController(AutohubSapSetupVerifier verifier, PartsSapReconciliationService reconcile)
    {
        _verifier = verifier;
        _reconcile = reconcile;
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
