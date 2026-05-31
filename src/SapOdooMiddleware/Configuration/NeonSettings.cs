namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Connection settings for the Neon (PostgreSQL) database that backs the
/// NeonProducts / NeonPriceLists / NeonLiquiMolyProducts tables consumed by the
/// existing Neon → Odoo automation.
/// </summary>
public class NeonSettings
{
    public const string SectionName = "Neon";

    /// <summary>Npgsql connection string for the Neon Postgres database.</summary>
    public string ConnectionString { get; set; } = string.Empty;
}
