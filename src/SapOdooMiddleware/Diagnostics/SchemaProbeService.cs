using Microsoft.Extensions.Options;
using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Diagnostics;

/// <summary>
/// Startup schema probe. Runs the actual SQL shapes the code depends on (with LIMIT 0) and checks
/// the DB constraints the code writes against, so a schema mismatch fails loudly at deploy instead
/// of as a silent runtime exception every poll. Registered before the workers it guards.
/// </summary>
public sealed class SchemaProbeService : IHostedService
{
    private static readonly string[] RequiredStatuses =
        { "uploaded", "extracting", "extracted", "reviewed", "completed", "failed" };

    private readonly CompaniesOptions _companies;
    private readonly SchemaGuard _guard;
    private readonly ILogger<SchemaProbeService> _logger;

    public SchemaProbeService(IOptions<CompaniesOptions> companies, SchemaGuard guard, ILogger<SchemaProbeService> logger)
    {
        _companies = companies.Value;
        _guard = guard;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await ProbeAutohubMatchAsync(ct);
        foreach (var (key, cfg) in _companies.Companies)
            await ProbeStatusConstraintAsync(key, cfg.Neon.ConnectionString, ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task ProbeAutohubMatchAsync(CancellationToken ct)
    {
        if (!_companies.Companies.TryGetValue(CompanyContext.AutohubKey, out var cfg)
            || string.IsNullOrWhiteSpace(cfg.Neon.ConnectionString))
            return;

        // Param-free LIMIT 0 queries — we only need them to bind against the real columns/joins.
        var probes = new[]
        {
            ("Tier-1 OEM (oitm_cross_reference⋈oitm)", """
                SELECT o.item_code FROM oitm_cross_reference xref
                JOIN oitm o ON o.id = xref.oitm_id
                WHERE xref.oem_number = ANY(ARRAY['__probe__']) AND xref.reference_type = 'oem' LIMIT 0;
                """),
            ("Tier-2 article (oitm.article_number/febi_article_no)", """
                SELECT item_code FROM oitm WHERE article_number = '__probe__' OR febi_article_no = '__probe__' LIMIT 0;
                """),
            ("Tier-2 germax fallback (neon_germax_products)", """
                SELECT item_code FROM neon_germax_products WHERE germax_article_number = '__probe__' AND is_active = true LIMIT 0;
                """),
        };

        try
        {
            await using var conn = new NpgsqlConnection(cfg.Neon.ConnectionString);
            await conn.OpenAsync(ct);
            foreach (var (name, sql) in probes)
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteScalarAsync(ct);
                _logger.LogDebug("Schema probe OK: {Probe}", name);
            }
            _guard.AutohubMatchOk = true;
            _logger.LogInformation("Schema probe: Autohub auto-match SQL OK ({Count} shapes).", probes.Length);
        }
        catch (PostgresException pex) when (pex.SqlState is "42703" or "42P01")
        {
            _guard.AutohubMatchOk = false;
            _logger.LogCritical(pex,
                "[FTL] Schema mismatch (Autohub auto-match): {SqlState} {Message}. AutoMatchWorker will stay idle until the parts_catalog schema is fixed and the service restarted.",
                pex.SqlState, pex.MessageText);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Schema probe (Autohub auto-match) could not run (connectivity?); leaving the worker enabled.");
        }
    }

    private async Task ProbeStatusConstraintAsync(string tenant, string? connStr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connStr)) return;
        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);
            const string sql = "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'staging_document_Status_check';";
            await using var cmd = new NpgsqlCommand(sql, conn);
            if (await cmd.ExecuteScalarAsync(ct) is not string def)
            {
                _logger.LogInformation("Schema probe [{Tenant}]: no staging_document_Status_check constraint (ok).", tenant);
                return;
            }

            var missing = RequiredStatuses.Where(s => !def.Contains($"'{s}'", StringComparison.Ordinal)).ToList();
            if (missing.Count > 0)
                _logger.LogCritical(
                    "[FTL] Schema mismatch [{Tenant}]: staging_document_Status_check is missing allowed value(s) {Missing}. The app writes these statuses; Complete Review will 23514. Apply migration 2026-06-06__lubes_status_constraint_fix.sql. Def: {Def}",
                    tenant, string.Join(", ", missing), def);
            else
                _logger.LogInformation("Schema probe [{Tenant}]: staging_document_Status_check OK.", tenant);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Schema probe [{Tenant}]: status-constraint check could not run.", tenant);
        }
    }
}
