using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Controllers;

/// <summary>Autohub operational/admin endpoints (API-key protected via middleware).</summary>
[ApiController]
[Route("api/autohub")]
public class AutohubAdminController : ControllerBase
{
    private readonly AutohubSapSetupVerifier _verifier;

    public AutohubAdminController(AutohubSapSetupVerifier verifier) => _verifier = verifier;

    /// <summary>
    /// Pre-flight check that the Autohub SAP company has the master-data item-create assumes
    /// (price lists, item groups, OITM UDFs, VAT groups, UoM group 1). Read-only.
    /// </summary>
    [HttpGet("verify-sap-setup")]
    public async Task<IActionResult> VerifySapSetup(CancellationToken ct) =>
        Ok(await _verifier.VerifyAsync(ct));
}
