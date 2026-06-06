using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>Outcome of a bridge link attempt (never throws on a blocked/missing row — the caller logs).</summary>
public enum NeonBridgeLinkStatus { Linked, AlreadyLinkedSame, BlockedByExisting, NotFound }

public sealed record NeonBridgeLinkResult(NeonBridgeLinkStatus Status, string? ExistingItemCode)
{
    public bool Written => Status == NeonBridgeLinkStatus.Linked;
}

/// <summary>A donor parts_catalog row with the fields needed to gate a supplier-identity match.</summary>
public sealed record OitmRow(long Id, string? ItemCode, string? ArticleNumber, string? SupplierName);

public interface INeonBridgeService
{
    /// <summary>
    /// Reads the SAP ItemCode currently linked to a parts_catalog <c>oitm</c> row, or null when the row is
    /// unlinked (item_code NULL/empty) or missing. Used to detect Path C1 — a borrowed/direct enrichment
    /// whose donor row is ALREADY a SAP item — so the line auto-matches instead of minting a duplicate.
    /// </summary>
    Task<string?> GetItemCodeAsync(int neonOitmId, CancellationToken ct);

    /// <summary>Reads the donor row (item_code + supplier_name + article) so the router can enforce supplier identity. Null if missing.</summary>
    Task<OitmRow?> GetOitmRowAsync(long neonOitmId, CancellationToken ct);

    /// <summary>
    /// Cross-supplier create-new: mint a NEW own-identity oitm row for the freshly-created SAP item (under
    /// our supplier), copying the donor's canonical OEM + OEM cross-references so future invoices auto-match
    /// it — WITHOUT touching the donor. Returns the new oitm id, or null if the donor row was not found.
    /// </summary>
    Task<long?> CreateOwnIdentityRowAsync(long donorOitmId, string sapItemCode, string source,
        string? articleNumber, string? supplierName, CancellationToken ct);

    /// <summary>
    /// Links a freshly-created SAP item back to its pre-enriched parts_catalog row by stamping the SAP
    /// ItemCode onto <c>oitm</c> (only WHERE item_code IS NULL — never overwrites a populated value).
    /// This is the middleware's ONLY write to the catalog; the enrichment service (DGX) owns row
    /// creation/population. Idempotent, and does NOT throw on a blocked/missing row — it returns a
    /// <see cref="NeonBridgeLinkResult"/> the caller logs (a throw here could provoke a duplicate on retry).
    /// </summary>
    Task<NeonBridgeLinkResult> LinkAsync(int neonOitmId, string sapItemCode, CancellationToken ct);
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

    public async Task<string?> GetItemCodeAsync(int neonOitmId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        const string sql = "SELECT item_code FROM oitm WHERE id = @id LIMIT 1;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", neonOitmId);
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result is null or DBNull) return null;
        var code = (string)result;
        return string.IsNullOrWhiteSpace(code) ? null : code;
    }

    public async Task<OitmRow?> GetOitmRowAsync(long neonOitmId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        const string sql = "SELECT id, item_code, article_number, supplier_name FROM oitm WHERE id = @id LIMIT 1;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", neonOitmId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new OitmRow(
            Id:            r.GetInt64(0),
            ItemCode:      r.IsDBNull(1) ? null : r.GetString(1),
            ArticleNumber: r.IsDBNull(2) ? null : r.GetString(2),
            SupplierName:  r.IsDBNull(3) ? null : r.GetString(3));
    }

    public async Task<long?> CreateOwnIdentityRowAsync(long donorOitmId, string sapItemCode, string source,
        string? articleNumber, string? supplierName, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        // Insert the new row, copying canonical_oem_number (and article fallback) from the donor. Defaults
        // handle cleanup_status / is_kit / create_date / write_date / id. Donor is left untouched.
        const string insert = """
            INSERT INTO oitm (article_number, supplier_name, canonical_oem_number, item_code, tecdoc_article_id, source)
            SELECT COALESCE(@article, d.article_number), @supplier, d.canonical_oem_number, @itemCode, NULL, @source
            FROM oitm d WHERE d.id = @donor
            RETURNING id;
            """;
        long newId;
        await using (var cmd = new NpgsqlCommand(insert, conn))
        {
            cmd.Parameters.AddWithValue("article", (object?)articleNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("supplier", (object?)supplierName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("itemCode", sapItemCode);
            cmd.Parameters.AddWithValue("source", source);
            cmd.Parameters.AddWithValue("donor", donorOitmId);
            var res = await cmd.ExecuteScalarAsync(ct);
            if (res is null or DBNull)
            {
                _logger.LogWarning("Cross-supplier: donor oitm {Donor} not found; cannot mint own-identity row for {Code}.", donorOitmId, sapItemCode);
                return null;
            }
            newId = Convert.ToInt64(res);
        }

        // Carry the donor's OEM cross-references onto the new row (new id → no conflicts possible).
        const string copy = """
            INSERT INTO oitm_cross_reference (oitm_id, oem_number, reference_type)
            SELECT @to, oem_number, reference_type
            FROM oitm_cross_reference WHERE oitm_id = @from AND reference_type = 'oem';
            """;
        await using (var cmd = new NpgsqlCommand(copy, conn))
        {
            cmd.Parameters.AddWithValue("to", newId);
            cmd.Parameters.AddWithValue("from", donorOitmId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        _logger.LogInformation(
            "Cross-supplier: minted own-identity oitm {NewId} (supplier {Supplier}, ItemCode {Code}); copied OEMs from donor {Donor}.",
            newId, supplierName, sapItemCode, donorOitmId);
        return newId;
    }

    public async Task<NeonBridgeLinkResult> LinkAsync(int neonOitmId, string sapItemCode, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        const string update = """
            UPDATE oitm SET item_code = @code, write_date = NOW()
            WHERE id = @id AND (item_code IS NULL OR item_code = '');
            """;
        await using (var cmd = new NpgsqlCommand(update, conn))
        {
            cmd.Parameters.AddWithValue("code", sapItemCode);
            cmd.Parameters.AddWithValue("id", neonOitmId);
            if (await cmd.ExecuteNonQueryAsync(ct) == 1)
            {
                _logger.LogInformation("Neon bridge linked oitm id {OitmId} → ItemCode {ItemCode}.", neonOitmId, sapItemCode);
                return new NeonBridgeLinkResult(NeonBridgeLinkStatus.Linked, sapItemCode);
            }
        }

        // No row updated — classify (missing / already-linked-same / conflict). Never throw: the SAP
        // item already exists, so we must not provoke a retry; the caller logs and an admin reconciles.
        const string check = "SELECT item_code FROM oitm WHERE id = @id;";
        await using var read = new NpgsqlCommand(check, conn);
        read.Parameters.AddWithValue("id", neonOitmId);
        var existing = await read.ExecuteScalarAsync(ct);

        if (existing is null)
        {
            _logger.LogWarning("Neon bridge: oitm id {OitmId} not found; cannot link {ItemCode}.", neonOitmId, sapItemCode);
            return new NeonBridgeLinkResult(NeonBridgeLinkStatus.NotFound, null);
        }

        var current = existing is DBNull ? null : (string)existing;
        if (string.Equals(current, sapItemCode, StringComparison.Ordinal))
        {
            _logger.LogInformation("Neon bridge: oitm id {OitmId} already linked to {ItemCode} (idempotent).", neonOitmId, sapItemCode);
            return new NeonBridgeLinkResult(NeonBridgeLinkStatus.AlreadyLinkedSame, current);
        }

        _logger.LogError(
            "Neon bridge: oitm id {OitmId} already linked to '{Existing}', refusing to overwrite with '{New}'. Likely a duplicate SAP item — reconcile.",
            neonOitmId, current, sapItemCode);
        return new NeonBridgeLinkResult(NeonBridgeLinkStatus.BlockedByExisting, current);
    }
}
