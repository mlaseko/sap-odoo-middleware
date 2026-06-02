using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Scoped <see cref="ICompanyContext"/>. For the Lubes tenant it surfaces a <see cref="CompanyConfig"/>
/// backed by the existing top-level SapB1/Odoo/Neon/DocumentIngestion/Classifier sections, so Lubes
/// credentials are never duplicated into the Companies section and Lubes code paths are unaffected.
/// For Autohub it returns the <c>Companies:Autohub</c> entry. The active key is set by middleware
/// (from URL) or by the Autohub worker (explicitly).
/// </summary>
public sealed class CompanyContext : ICompanyContext
{
    public const string LubesKey   = "Lubes";
    public const string AutohubKey = "Autohub";

    private readonly IOptions<NeonSettings>              _neon;
    private readonly IOptions<DocumentIngestionSettings> _docIngestion;
    private readonly IOptions<ClassifierSettings>        _classifier;
    private readonly IOptions<SapB1Settings>             _sap;
    private readonly IOptions<OdooSettings>              _odoo;
    private readonly CompaniesOptions                    _companies;

    private string _key = LubesKey;
    private CompanyConfig? _cached;

    public CompanyContext(
        IOptions<NeonSettings> neon,
        IOptions<DocumentIngestionSettings> docIngestion,
        IOptions<ClassifierSettings> classifier,
        IOptions<SapB1Settings> sap,
        IOptions<OdooSettings> odoo,
        IOptions<CompaniesOptions> companies)
    {
        _neon         = neon;
        _docIngestion = docIngestion;
        _classifier   = classifier;
        _sap          = sap;
        _odoo         = odoo;
        _companies    = companies.Value;
    }

    public string CurrentCompanyKey => _key;

    public CompanyConfig Current => _cached ??= Build(_key);

    /// <summary>
    /// Sets the active tenant for this scope. Unknown keys fall back to Lubes (default) so a stray
    /// path can never resolve to an unconfigured tenant. Clears the cached config.
    /// </summary>
    public void SetCompany(string? companyKey)
    {
        _key = NormalizeKey(companyKey);
        _cached = null;
    }

    /// <summary>Pure URL → CompanyKey rule (used by middleware and unit-tested directly).</summary>
    public static string ResolveCompanyKey(PathString path)
    {
        var p = path.Value ?? string.Empty;
        if (HasSegment(p, "/autohub") || HasSegment(p, "/api/autohub"))
            return AutohubKey;
        return LubesKey;
    }

    private static bool HasSegment(string path, string prefix) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeKey(string? key) =>
        string.Equals(key, AutohubKey, StringComparison.OrdinalIgnoreCase) ? AutohubKey : LubesKey;

    private CompanyConfig Build(string key)
    {
        if (key == AutohubKey)
        {
            if (_companies.Companies.TryGetValue(AutohubKey, out var autohub))
                return autohub;
            throw new InvalidOperationException(
                "Autohub tenant requested but 'Companies:Autohub' is not configured in appsettings.");
        }

        // Lubes: project the existing top-level sections so Lubes config stays single-sourced.
        return new CompanyConfig
        {
            DisplayName       = _companies.Companies.TryGetValue(LubesKey, out var lubesMeta) && !string.IsNullOrWhiteSpace(lubesMeta.DisplayName)
                                ? lubesMeta.DisplayName : "Molas Lubes",
            IsDefault         = true,
            SapB1             = _sap.Value,
            Odoo              = _odoo.Value,
            Neon              = _neon.Value,
            DocumentIngestion = _docIngestion.Value,
            Classifier        = _classifier.Value,
            VisionModel       = "qwen2.5vl:32b-invoice",
            VisionEndpoint    = "/extract_invoice",
        };
    }
}
