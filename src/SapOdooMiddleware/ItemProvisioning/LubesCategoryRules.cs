namespace SapOdooMiddleware.ItemProvisioning;

/// <summary>
/// Rules for the Odoo product category of a provisioned LIQUI MOLY / Meguin item.
///
/// Odoo's internal product category is the master for the whole catalogue: the LIQUI MOLY tree imported
/// with external ids <c>__import__.lm_cat_&lt;family&gt;__&lt;sub_category&gt;</c>, whose families line up
/// with the SAP item groups. Odoo copies <c>NeonProducts.OdooCategoryExternalId</c> onto the product
/// ("Backfill Categories"), so whatever is written there must be a precise sub-category of that tree:
/// never a family node (<c>lm_cat_additives</c>) or the family's catch-all (<c>lm_cat_additives__additives</c>),
/// which is where 53 additives ended up when DGX was given only the family as a hint.
/// </summary>
public static class LubesCategoryRules
{
    public const string ExternalIdPrefix = "__import__.lm_cat_";

    /// <summary>
    /// SAP item groups (OITB.ItmsGrpCod) an item of each LIQUI MOLY family may be in. A family not listed
    /// here (e.g. accessories, marine) is not cross-checked. 110 "Motor Bike" is allowed for the families
    /// motorbike products live in (engine oils, additives, vehicle care), as the Layer-1 rule assigns it;
    /// 106 "Commercial vehicles" for engine oils. Keep this in step with the live OITB groups.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int[]> AllowedGroupsByFamily = new Dictionary<string, int[]>
    {
        ["engine_oils"]                    = new[] { 105, 106, 110 },
        ["gear_oils_transmission_fluids"]  = new[] { 107 },
        ["greases"]                        = new[] { 107, 117 },
        ["additives"]                      = new[] { 109, 110 },
        ["vehicle_care"]                   = new[] { 111, 110 },
        ["workshop_pro_line"]              = new[] { 112 },
        ["oils_industrial_other_fluids"]   = new[] { 118 },
        ["pastes"]                         = new[] { 119 },
        ["service_products"]               = new[] { 120, 104 },
        ["repair_aids"]                    = new[] { 104 },
        ["adhesives_sealants"]             = new[] { 121 },
    };

    /// <summary>The family key of a LIQUI MOLY category external id ("additives"), or null.</summary>
    public static string? FamilyKey(string? externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        var id = externalId.Trim();
        if (!id.StartsWith(ExternalIdPrefix, StringComparison.Ordinal)) return null;
        var rest = id[ExternalIdPrefix.Length..];
        var sep = rest.IndexOf("__", StringComparison.Ordinal);
        var family = sep < 0 ? rest : rest[..sep];
        return family.Length == 0 ? null : family;
    }

    /// <summary>
    /// Why this external id must not be stored as an item's Odoo category, or null when it is fine:
    /// outside the LIQUI MOLY tree, a family node, or the family's catch-all sub-category.
    /// </summary>
    public static string? CategoryProblem(string? externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return "no Odoo category";
        var id = externalId.Trim();
        var family = FamilyKey(id);
        if (family is null)
            return $"'{id}' is not a LIQUI MOLY category ({ExternalIdPrefix}...)";
        var rest = id[ExternalIdPrefix.Length..];
        var sep = rest.IndexOf("__", StringComparison.Ordinal);
        if (sep < 0)
            return $"'{id}' is a whole family, not a precise sub-category";
        var sub = rest[(sep + 2)..];
        if (sub.Length == 0)
            return $"'{id}' has no sub-category";
        if (string.Equals(sub, family, StringComparison.Ordinal))
            return $"'{id}' is the family's catch-all sub-category; pick the precise one";
        return null;
    }

    /// <summary>
    /// Why the Odoo category and the SAP item group disagree, or null when they fit (or the family is not
    /// cross-checked, or either side is missing).
    /// </summary>
    public static string? GroupMismatch(string? externalId, int? groupCode)
    {
        var family = FamilyKey(externalId);
        if (family is null || groupCode is null) return null;
        if (!AllowedGroupsByFamily.TryGetValue(family, out var allowed)) return null;
        if (Array.IndexOf(allowed, groupCode.Value) >= 0) return null;
        return $"Odoo category '{externalId}' (family {family}) does not fit SAP item group {groupCode} "
               + $"(expected {string.Join(" or ", allowed)})";
    }

    /// <summary>
    /// The category hint sent to DGX /classify. The scraped family alone ("Additives") steers DGX to the
    /// catch-all, so the scraped sub-category is added, and Pro-Line products (often listed under
    /// "Additives" on liqui-moly.com) are hinted as Workshop Pro-Line.
    /// </summary>
    public static string? BuildCategoryHint(string? category, string? subCategory, string? productName)
    {
        var family = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        var sub = string.IsNullOrWhiteSpace(subCategory) ? null : subCategory.Trim();
        var isProLine = !string.IsNullOrWhiteSpace(productName)
                        && System.Text.RegularExpressions.Regex.IsMatch(productName, @"^\s*pro-?line\b",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (isProLine)
            family = "Workshop Pro-Line";
        if (sub is not null && !string.Equals(sub, family, StringComparison.OrdinalIgnoreCase))
            return family is null ? sub : $"{family} / {sub}";
        return family;
    }
}
