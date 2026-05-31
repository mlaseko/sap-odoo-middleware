using Microsoft.AspNetCore.Mvc;
using SapOdooMiddleware.ItemProvisioning;

namespace SapOdooMiddleware.Controllers;

/// <summary>
/// Item Provisioning endpoint. Creates one Liqui Moly item master end-to-end
/// (SAP B1 is the system of record; Neon rows feed the Neon → Odoo automation).
///
/// POST /api/items — body (snake_case): { "article_number", "eur_cost", "eur_tzs_rate_override?", "dry_run?" }
/// </summary>
[ApiController]
[Route("api/items")]
public class ItemsController : ControllerBase
{
    private readonly ILubesItemProvisioningService _provisioning;
    private readonly ILogger<ItemsController> _logger;

    public ItemsController(
        ILubesItemProvisioningService provisioning,
        ILogger<ItemsController> logger)
    {
        _provisioning = provisioning;
        _logger = logger;
    }

    /// <summary>Create one Lubes item end-to-end from an article number + EUR cost.</summary>
    [HttpPost]
    public async Task<ActionResult<LubesProvisioningResult>> Provision(
        [FromBody] LubesProvisioningRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Item provisioning requested — ArticleNumber={ArticleNumber}, EurCost={EurCost}, DryRun={DryRun}",
            request.ArticleNumber, request.EurCost, request.DryRun);

        var result = await _provisioning.ProvisionAsync(request, ct);

        return result.Status switch
        {
            "created"      => Ok(result),
            "exists"       => Ok(result),
            "dry_run"      => Ok(result),
            "needs_review" => Accepted(result),
            "failed"       => StatusCode(500, result),
            _              => Ok(result),
        };
    }
}
