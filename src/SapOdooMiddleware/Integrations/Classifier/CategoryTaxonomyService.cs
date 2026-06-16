using System.Text.Json;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Integrations.Classifier;

public interface ICategoryTaxonomy
{
    /// <summary>True when a taxonomy bundle is loaded (validation active). False → fail-open.</summary>
    bool IsLoaded { get; }

    /// <summary>Number of external_ids currently loaded.</summary>
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

    /// <summary>Re-read the bundle from disk (used by the file watcher and the admin reload endpoint).</summary>
    void Reload();
}

/// <summary>
/// Loads the authoritative Odoo category taxonomy (external_ids) and validates ids against it. Guards the
/// "accept low confidence" path from persisting a category that DGX returned off a stale taxonomy. Reloads
/// without a restart (file watcher + admin endpoint). Fail-open: with no bundle, every id is valid.
/// </summary>
public sealed class CategoryTaxonomyService : ICategoryTaxonomy, IDisposable
{
    private readonly string? _path;
    private readonly ILogger<CategoryTaxonomyService> _logger;
    private readonly object _reloadLock = new();

    private volatile HashSet<string> _ids = new(StringComparer.Ordinal);
    private DateTimeOffset? _loadedAt;
    private FileSystemWatcher? _watcher;
    private DateTime _lastReloadUtc = DateTime.MinValue;

    public bool IsLoaded => _ids.Count > 0;
    public int Count => _ids.Count;
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
        !IsLoaded || (!string.IsNullOrWhiteSpace(externalId) && _ids.Contains(externalId!));

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
                    _ids = new HashSet<string>(StringComparer.Ordinal);
                    _loadedAt = null;
                    return;
                }

                var set = new HashSet<string>(StringComparer.Ordinal);
                using var doc = JsonDocument.Parse(File.ReadAllText(_path));
                LoadFrom(doc.RootElement, set);

                _ids = set;                       // atomic swap; readers see old or new, both valid
                _loadedAt = DateTimeOffset.UtcNow;
                _logger.LogInformation(
                    "Odoo taxonomy validator: loaded {Count} categories from {Path}.", set.Count, _path);
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
        // Editors/copies fire several events; debounce and let the writer finish before re-reading.
        if ((DateTime.UtcNow - _lastReloadUtc).TotalSeconds < 2) return;
        _lastReloadUtc = DateTime.UtcNow;
        Task.Delay(500).ContinueWith(_ => Reload());
    }

    private static void LoadFrom(JsonElement root, HashSet<string> set)
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
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) set.Add(s!);
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
                {
                    var s = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) set.Add(s!);
                }
        }
    }

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch { /* ignore */ }
    }
}
