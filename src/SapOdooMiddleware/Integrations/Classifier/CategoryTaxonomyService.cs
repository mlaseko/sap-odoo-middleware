using System.Text.Json;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Integrations.Classifier;

/// <summary>One Odoo category from the taxonomy bundle, keyed by <see cref="ExternalId"/>.</summary>
public sealed record CategoryEntry(string ExternalId, string? Name, string? Parent, string? FullPath);

public interface ICategoryTaxonomy
{
    /// <summary>True when a taxonomy bundle is loaded (validation active). False → fail-open.</summary>
    bool IsLoaded { get; }

    /// <summary>Number of categories currently loaded.</summary>
    int Count { get; }

    /// <summary>When the bundle was last (re)loaded, UTC; null if never loaded.</summary>
    DateTimeOffset? LoadedAt { get; }

    /// <summary>Configured bundle path (null if not configured).</summary>
    string? FilePath { get; }

    /// <summary>
    /// True if the external_id is acceptable: always true when no bundle is loaded (fail-open), otherwise
    /// only when the id is present in the loaded bundle.
    /// </summary>
    bool IsValidExternalId(string? externalId);

    /// <summary>Human-readable full path for a known external_id (falls back to name), or null.</summary>
    string? FullPathFor(string? externalId);

    /// <summary>All loaded categories (for the review-UI picker). Empty when no bundle is loaded.</summary>
    IReadOnlyList<CategoryEntry> All();

    /// <summary>Re-read the bundle from disk (used by the file watcher and the admin reload endpoint).</summary>
    void Reload();
}

/// <summary>
/// Loads the authoritative Odoo category taxonomy and validates ids against it. Guards the
/// "accept low confidence" path from persisting a category that DGX returned off a stale taxonomy.
/// Reloads without a restart (file watcher + admin endpoint). Fail-open: with no bundle, every id is valid.
/// </summary>
public sealed class CategoryTaxonomyService : ICategoryTaxonomy, IDisposable
{
    private readonly string? _path;
    private readonly ILogger<CategoryTaxonomyService> _logger;
    private readonly object _reloadLock = new();

    // Keyed by external_id → entry (name/parent/full_path). Swapped atomically on reload.
    private volatile Dictionary<string, CategoryEntry> _categories = new(StringComparer.Ordinal);
    private DateTimeOffset? _loadedAt;
    private FileSystemWatcher? _watcher;
    private DateTime _lastReloadUtc = DateTime.MinValue;

    public bool IsLoaded => _categories.Count > 0;
    public int Count => _categories.Count;
    public DateTimeOffset? LoadedAt => _loadedAt;
    public string? FilePath => _path;

    public CategoryTaxonomyService(IOptions<CategoryTaxonomySettings> settings, ILogger<CategoryTaxonomyService> logger)
    {
        _logger = logger;
        _path = string.IsNullOrWhiteSpace(settings.Value.FilePath) ? null : settings.Value.FilePath;

        if (_path is null)
        {
            _logger.LogInformation(
                "Odoo taxonomy validator: no bundle configured (CategoryTaxonomy:FilePath empty) — validation disabled (fail-open).");
            return;
        }

        Reload();
        TrySetupWatcher();
    }

    public bool IsValidExternalId(string? externalId) =>
        !IsLoaded || (!string.IsNullOrWhiteSpace(externalId) && _categories.ContainsKey(externalId!));

    public string? FullPathFor(string? externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        return _categories.TryGetValue(externalId!, out var e) ? (e.FullPath ?? e.Name) : null;
    }

    public IReadOnlyList<CategoryEntry> All() =>
        _categories.Values.OrderBy(e => e.FullPath ?? e.Name ?? e.ExternalId, StringComparer.OrdinalIgnoreCase).ToList();

    public void Reload()
    {
        if (_path is null) return;
        lock (_reloadLock)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    _logger.LogWarning(
                        "Odoo taxonomy validator: bundle not found at {Path} — validation disabled (fail-open).", _path);
                    _categories = new Dictionary<string, CategoryEntry>(StringComparer.Ordinal);
                    _loadedAt = null;
                    return;
                }

                var map = new Dictionary<string, CategoryEntry>(StringComparer.Ordinal);
                using var doc = JsonDocument.Parse(File.ReadAllText(_path));
                LoadFrom(doc.RootElement, map);

                _categories = map;                // atomic swap; readers see old or new, both valid
                _loadedAt = DateTimeOffset.UtcNow;
                _logger.LogInformation(
                    "Odoo taxonomy validator: loaded {Count} categories from {Path}.", map.Count, _path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Odoo taxonomy validator: failed to load bundle from {Path} — keeping previous state.", _path);
            }
        }
    }

    private void TrySetupWatcher()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path!);
            var file = Path.GetFileName(_path!);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file) || !Directory.Exists(dir))
                return;

            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
            _logger.LogInformation("Odoo taxonomy validator: watching {Path} for changes (auto-reload).", _path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Odoo taxonomy validator: could not start file watcher; use POST /api/admin/reload-taxonomy instead.");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if ((DateTime.UtcNow - _lastReloadUtc).TotalSeconds < 2) return;
        _lastReloadUtc = DateTime.UtcNow;
        Task.Delay(500).ContinueWith(_ => Reload());
    }

    private static void LoadFrom(JsonElement root, Dictionary<string, CategoryEntry> map)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            // [ { "external_id": "...", "name": "...", "parent": "...", "full_path": "..." }, ... ]
            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var id = Str(el, "external_id", "externalId", "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                map[id!] = new CategoryEntry(
                    id!,
                    Str(el, "name"),
                    Str(el, "parent", "parent_id"),
                    Str(el, "full_path", "fullPath", "path"));
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            // { "<name>": "<external_id>", ... } — value is the id, key is the (display) name.
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var id = prop.Value.GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                map[id!] = new CategoryEntry(id!, prop.Name, null, prop.Name);
            }
        }
    }

    private static string? Str(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch { /* ignore */ }
    }
}
