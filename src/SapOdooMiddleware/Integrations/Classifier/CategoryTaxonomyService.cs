using System.Text.Json;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Integrations.Classifier;

public interface ICategoryTaxonomy
{
    /// <summary>True when a taxonomy file was loaded (validation active). False → fail-open.</summary>
    bool IsLoaded { get; }

    /// <summary>
    /// Returns true if the external_id is acceptable: always true when no taxonomy is loaded (fail-open),
    /// otherwise true only when the id is present in the loaded taxonomy.
    /// </summary>
    bool IsValidExternalId(string? externalId);
}

/// <summary>
/// Loads the authoritative Odoo category taxonomy (external_ids) once at startup and validates ids against
/// it. Guards the review paths that persist an external_id without a live DGX classification — e.g. if DGX
/// is running on a slightly stale taxonomy, "accept low confidence" could otherwise persist a removed
/// category reference. Fail-open: if no file is configured/loadable, every id is treated as valid.
/// </summary>
public sealed class CategoryTaxonomyService : ICategoryTaxonomy
{
    private readonly HashSet<string> _externalIds = new(StringComparer.Ordinal);

    public bool IsLoaded => _externalIds.Count > 0;

    public CategoryTaxonomyService(IOptions<CategoryTaxonomySettings> settings, ILogger<CategoryTaxonomyService> logger)
    {
        var path = settings.Value.FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            logger.LogInformation(
                "Odoo category taxonomy not configured (CategoryTaxonomy:FilePath empty); category external_id "
                + "validation is disabled (fail-open).");
            return;
        }

        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("Odoo taxonomy file not found at {Path}; external_id validation disabled.", path);
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            LoadFrom(doc.RootElement);
            logger.LogInformation(
                "Loaded Odoo category taxonomy: {Count} external_id(s) from {Path}.", _externalIds.Count, path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load Odoo taxonomy from {Path}; external_id validation disabled.", path);
        }
    }

    public bool IsValidExternalId(string? externalId) =>
        !IsLoaded || (!string.IsNullOrWhiteSpace(externalId) && _externalIds.Contains(externalId!));

    private void LoadFrom(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            // [ { "external_id": "...", "name": "..." }, ... ]
            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                foreach (var key in new[] { "external_id", "externalId", "id" })
                {
                    if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        Add(v.GetString());
                        break;
                    }
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            // { "<name>": "<external_id>", ... } — take the values.
            foreach (var prop in root.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    Add(prop.Value.GetString());
        }
    }

    private void Add(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id)) _externalIds.Add(id!);
    }
}
