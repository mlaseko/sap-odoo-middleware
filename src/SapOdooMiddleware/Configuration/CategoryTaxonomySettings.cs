namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Optional Odoo category taxonomy used to defensively validate category external_ids before they are
/// persisted (the "accept low confidence" and manual-override review paths). When no file is configured
/// or it can't be loaded, validation is disabled (fail-open) so nothing is blocked.
/// </summary>
public class CategoryTaxonomySettings
{
    public const string SectionName = "CategoryTaxonomy";

    /// <summary>
    /// Path to the authoritative Odoo category bundle (the same 95-category export the review UI uses).
    /// Supported JSON shapes: an array of objects with an "external_id"/"externalId"/"id" field, or a flat
    /// { name: external_id } map. Empty → validation disabled.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;
}
