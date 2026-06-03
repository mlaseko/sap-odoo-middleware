using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.Autohub;

public interface INeonBridgeService
{
    /// <summary>
    /// Mirrors a newly-created SAP item into parts_catalog so future auto-match finds it:
    /// upserts the oitm row (item_code + U_Article_No for Tier-2) and one oitm_cross_reference row
    /// per OEM (for Tier-1). Idempotent.
    /// </summary>
    Task PublishAsync(string itemCode, string articleNumber, IReadOnlyList<string> oemNumbers,
        string? description, string? brand, CancellationToken ct);
}

/// <summary>
/// Writes the Neon (parts_catalog) mirror after a SAP create. Connection per-tenant via
/// ICompanyContext.
///
/// NOTE: targets the same parts_catalog tables auto-match reads (§6.1) — oitm (item_code,
/// "U_Article_No", and best-effort description/brand columns) and oitm_cross_reference
/// (oem_number, item_code). Column names are the spec's and are flagged assumptions; align them
/// with OitmMatchRepository if the real mirror differs.
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

    public async Task PublishAsync(string itemCode, string articleNumber, IReadOnlyList<string> oemNumbers,
        string? description, string? brand, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string upsertItem = """
            INSERT INTO oitm (item_code, "U_Article_No", item_name, brand)
            VALUES (@code, @article, @desc, @brand)
            ON CONFLICT (item_code) DO UPDATE
              SET "U_Article_No" = EXCLUDED."U_Article_No",
                  item_name = COALESCE(EXCLUDED.item_name, oitm.item_name),
                  brand = COALESCE(EXCLUDED.brand, oitm.brand);
            """;
        await using (var cmd = new NpgsqlCommand(upsertItem, conn, tx))
        {
            cmd.Parameters.AddWithValue("code", itemCode);
            cmd.Parameters.AddWithValue("article", articleNumber);
            cmd.Parameters.AddWithValue("desc", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("brand", (object?)brand ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        const string insertXref = """
            INSERT INTO oitm_cross_reference (oem_number, item_code)
            VALUES (@oem, @code)
            ON CONFLICT (oem_number, item_code) DO NOTHING;
            """;
        foreach (var oem in oemNumbers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var cmd = new NpgsqlCommand(insertXref, conn, tx);
            cmd.Parameters.AddWithValue("oem", oem);
            cmd.Parameters.AddWithValue("code", itemCode);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _logger.LogInformation("Neon bridge published {ItemCode} ({XrefCount} OEM cross-refs).",
            itemCode, oemNumbers.Count);
    }
}
