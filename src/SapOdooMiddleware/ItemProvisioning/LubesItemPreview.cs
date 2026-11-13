using MolasLubes.Infrastructure.Integrations.LiquiMoly;

namespace SapOdooMiddleware.ItemProvisioning;

/// <summary>
/// Request to preview one article for a sales inquiry (Odoo: Sales ▸ Products, a code not in Odoo).
/// Bound from snake_case JSON: article_number, brand ("liqui_moly" | "meguin"; empty = Liqui Moly, then Meguin).
/// </summary>
public record LubesItemPreviewRequest(string ArticleNumber, string? Brand = null);

/// <summary>
/// What a sales user and the approver see before an item exists: the scraped product and the Odoo category /
/// SAP item group provisioning WOULD give it. Never carries prices (no EUR cost is known at inquiry time).
/// A classifier problem does not fail the preview: it is reported in <see cref="ClassificationNote"/>.
/// </summary>
public record LubesItemPreviewResult(
    string  Status,                                // "found" | "not_found"
    string  ItemCode,
    string? Brand                       = null,    // "Liqui Moly" | "Meguin"
    string? ItemName                    = null,    // provisioning's "{code}-{Name}-{Pack}"
    LiquiMolyProductDto? Product        = null,
    string? OdooCategoryName            = null,
    string? OdooCategoryExternalId      = null,
    string? OdooCategoryFullPath        = null,
    double? OdooCategoryConfidence      = null,
    string? OdooCategorySource          = null,    // "rule" | "classifier"
    int?    SapItemGroupCode            = null,
    string? SapItemGroupName            = null,
    double? SapItemGroupConfidence      = null,
    string? SapItemGroupSource          = null,    // "rule" | "classifier"
    bool    NeedsReview                 = false,   // classification would go to review at provisioning time
    string? ClassificationNote          = null,
    List<string>? Candidates            = null,
    string? Message                     = null);
