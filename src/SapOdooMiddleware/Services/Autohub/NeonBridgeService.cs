using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.Autohub;

public interface INeonBridgeService
{
    /// <summary>
    /// Links a freshly-created SAP item back to its pre-enriched parts_catalog row by stamping the
    /// SAP ItemCode onto oitm. This is the middleware's ONLY write to the catalog — the enrichment
    /// service (DGX) owns row creation/population (with item_code = NULL until now). Idempotent:
    /// re-linking the same code is a no-op; linking a row that already carries a *different* code
    /// throws (data-integrity guard rather than silent clobber).
    /// </summary>
    Task LinkAsync(int neonOitmId, string sapItemCode, CancellationToken ct);
}

/// <summary>
/// Bridges a SAP item create to the parts_catalog mirror via a single targeted UPDATE
/// (oitm.item_code), so auto-match finds the item afterwards. Connection per-tenant via
/// ICompanyContext. No INSERTs and no oitm_cross_reference writes — those belong to the enrichment
/// pipeline, which has the data the middleware doesn't.
/// </summary>
public sealed class NeonBridgeService : INeonBridgeService
{
    private readonly ICompanyContext _company;
    private readonly ILogger<NeonBridgeService> _logger;

    public NeonBridgeService(ICompanyContext company, ILogger<NeonBridgeService> logger)
    {
        _company = company;
        _logger = logger;
    }

    private string ConnectionString => _company.Current.Neon.ConnectionString;

    public async Task LinkAsync(int neonOitmId, string sapItemCode, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        const string update = """
            UPDATE oitm SET item_code = @code, write_date = NOW()
            WHERE id = @id AND item_code IS NULL;
            """;
        await using (var cmd = new NpgsqlCommand(update, conn))
        {
            cmd.Parameters.AddWithValue("code", sapItemCode);
            cmd.Parameters.AddWithValue("id", neonOitmId);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows == 1)
            {
                _logger.LogInformation("Neon bridge linked oitm id {OitmId} → ItemCode {ItemCode}.", neonOitmId, sapItemCode);
                return;
            }
        }

        // No row updated — distinguish missing / already-linked-same (idempotent) / conflict.
        const string check = "SELECT item_code FROM oitm WHERE id = @id;";
        await using var read = new NpgsqlCommand(check, conn);
        read.Parameters.AddWithValue("id", neonOitmId);
        var existing = await read.ExecuteScalarAsync(ct);

        if (existing is null)
            throw new InvalidOperationException($"Neon bridge: oitm id {neonOitmId} not found.");
        if (existing is DBNull)
            throw new InvalidOperationException($"Neon bridge: oitm id {neonOitmId} update affected 0 rows unexpectedly.");

        var current = (string)existing;
        if (string.Equals(current, sapItemCode, StringComparison.Ordinal))
        {
            _logger.LogInformation("Neon bridge: oitm id {OitmId} already linked to {ItemCode} (idempotent).", neonOitmId, sapItemCode);
            return;
        }

        throw new InvalidOperationException(
            $"Neon bridge: oitm id {neonOitmId} is already linked to ItemCode '{current}', refusing to overwrite with '{sapItemCode}'.");
    }
}
