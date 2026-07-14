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

/// <summary>A donor candidate's local detail for the operator swap modal: its parts_catalog identity, image,
/// part type, kit flag, richness counts and its true OEM cross-references (reference_type='oem' only) — all
/// mirrored locally in <c>oitm</c>, so no TecDoc/DGX fetch is needed.</summary>
public sealed record DonorDetail(
    long OitmId, string? ItemCode, IReadOnlyList<string> Oems,
    string? Name, string? ImageUrl, string? PartComponent, bool IsKit,
    int SpecCount, int CompatibleVehiclesCount, int CategoriesCount);

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
    /// Resolve the parts_catalog <c>oitm</c> id for a donor by its (article_number, supplier_name) — the
    /// operator-swap key. Used by the "swap borrowed article" action to re-point a line to a chosen local
    /// donor without a DGX round-trip. Case/whitespace-insensitive; supplier null matches on article only.
    /// Returns null if no such row exists.
    /// </summary>
    Task<long?> FindOitmIdByArticleSupplierAsync(string articleNumber, string? supplierName, CancellationToken ct);

    /// <summary>
    /// Load a donor candidate's local detail (parts_catalog row id + item_code + its 'oem' cross-references)
    /// by (article, supplier) — for the operator swap modal's per-candidate OEM list. Null if not found.
    /// </summary>
    Task<DonorDetail?> GetDonorDetailAsync(string articleNumber, string? supplierName, CancellationToken ct);

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

    /// <summary>
    /// Manual create (no donor): INSERT a fresh own-identity <c>oitm</c> row for a newly-created SAP item,
    /// plus its OEM cross-references, so future invoices for this (supplier, article) auto-match instead of
    /// landing in needs_manual. Returns the new oitm id (or null if the insert produced no row).
    /// </summary>
    Task<long?> CreateFreshRowAsync(string sapItemCode, string articleNumber, string? supplierName,
        IReadOnlyList<string> oemNumbers, string source, CancellationToken ct);

    /// <summary>
    /// The donor row's true OEM cross-references (<c>reference_type = 'oem'</c> ONLY — never the
    /// <c>iam_equivalent</c> aftermarket rows), used to populate the SAP ItemName. Empty if none.
    /// </summary>
    Task<IReadOnlyList<string>> GetOemCrossReferencesAsync(long oitmId, CancellationToken ct);
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

    public async Task<long?> FindOitmIdByArticleSupplierAsync(string articleNumber, string? supplierName, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT id FROM oitm
            WHERE lower(btrim(article_number)) = lower(btrim(@art))
              AND (@sup IS NULL OR lower(btrim(supplier_name)) = lower(btrim(@sup)))
            ORDER BY id
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("art", articleNumber);
        cmd.Parameters.AddWithValue("sup", (object?)supplierName ?? DBNull.Value);
        var res = await cmd.ExecuteScalarAsync(ct);
        return res is null or DBNull ? null : Convert.ToInt64(res);
    }

    public async Task<DonorDetail?> GetDonorDetailAsync(string articleNumber, string? supplierName, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        long id;
        string? itemCode, name, imageUrl, partComponent;
        bool isKit;
        int specCount, vehiclesCount, categoriesCount;
        const string rowSql = """
            SELECT id, item_code, name, image_url, part_component,
                   COALESCE(is_kit, false),
                   COALESCE(spec_count, 0), COALESCE(compatible_vehicles_count, 0), COALESCE(categories_count, 0)
            FROM oitm
            WHERE lower(btrim(article_number)) = lower(btrim(@art))
              AND (@sup IS NULL OR lower(btrim(supplier_name)) = lower(btrim(@sup)))
            ORDER BY id
            LIMIT 1;
            """;
        await using (var cmd = new NpgsqlCommand(rowSql, conn))
        {
            cmd.Parameters.AddWithValue("art", articleNumber);
            cmd.Parameters.AddWithValue("sup", (object?)supplierName ?? DBNull.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            id              = r.GetInt64(0);
            itemCode        = r.IsDBNull(1) ? null : r.GetString(1);
            name            = r.IsDBNull(2) ? null : r.GetString(2);
            imageUrl        = r.IsDBNull(3) ? null : r.GetString(3);
            partComponent   = r.IsDBNull(4) ? null : r.GetString(4);
            isKit           = !r.IsDBNull(5) && r.GetBoolean(5);
            specCount       = r.GetInt32(6);
            vehiclesCount   = r.GetInt32(7);
            categoriesCount = r.GetInt32(8);
        }

        // The donor's true OEM cross-references (reference_type='oem' only — never 'iam_equivalent'),
        // excluding any leaked internal SKU (a value equal to an existing item_code).
        const string oemSql = """
            SELECT x.oem_number FROM oitm_cross_reference x
            WHERE x.oitm_id = @id AND x.reference_type = 'oem' AND x.oem_number IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM oitm o WHERE o.item_code = x.oem_number)
            ORDER BY x.oem_number;
            """;
        var oems = new List<string>();
        await using (var cmd = new NpgsqlCommand(oemSql, conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(0)) oems.Add(r.GetString(0));
        }

        return new DonorDetail(id, itemCode, oems, name, imageUrl, partComponent, isKit, specCount, vehiclesCount, categoriesCount);
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
        // Namespace guard: never copy a token that is actually one of our internal SKUs (an item_code) —
        // those are leaked-SKU contamination in the OEM space (the LR100xxx-in-'oem' rows) and must not
        // propagate into a new item's cross-refs / ItemName.
        const string copy = """
            INSERT INTO oitm_cross_reference (oitm_id, oem_number, reference_type)
            SELECT @to, x.oem_number, x.reference_type
            FROM oitm_cross_reference x
            WHERE x.oitm_id = @from AND x.reference_type = 'oem'
              AND NOT EXISTS (SELECT 1 FROM oitm o WHERE o.item_code = x.oem_number);
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

    public async Task<long?> CreateFreshRowAsync(string sapItemCode, string articleNumber, string? supplierName,
        IReadOnlyList<string> oemNumbers, string source, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var canonicalOem = oemNumbers.Count > 0 ? oemNumbers[0] : null;

        const string insert = """
            INSERT INTO oitm (article_number, supplier_name, canonical_oem_number, item_code, tecdoc_article_id, source)
            VALUES (@article, @supplier, @canonical, @itemCode, NULL, @source)
            RETURNING id;
            """;
        long newId;
        await using (var cmd = new NpgsqlCommand(insert, conn))
        {
            cmd.Parameters.AddWithValue("article", articleNumber);
            cmd.Parameters.AddWithValue("supplier", (object?)supplierName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("canonical", (object?)canonicalOem ?? DBNull.Value);
            cmd.Parameters.AddWithValue("itemCode", sapItemCode);
            cmd.Parameters.AddWithValue("source", source);
            var res = await cmd.ExecuteScalarAsync(ct);
            if (res is null or DBNull)
            {
                _logger.LogWarning("Manual create: fresh oitm insert for {Code} produced no row.", sapItemCode);
                return null;
            }
            newId = Convert.ToInt64(res);
        }

        // Carry the line's OEM numbers as cross-references so Tier-1 OEM auto-match finds the item later.
        if (oemNumbers.Count > 0)
        {
            // Namespace guard: skip any line OEM that collides with an existing internal SKU (item_code) —
            // keeps our primary keys out of the OEM namespace.
            const string xref = """
                INSERT INTO oitm_cross_reference (oitm_id, oem_number, reference_type)
                SELECT @id, @oem, 'oem'
                WHERE NOT EXISTS (SELECT 1 FROM oitm o WHERE o.item_code = @oem);
                """;
            foreach (var oem in oemNumbers)
            {
                await using var cmd = new NpgsqlCommand(xref, conn);
                cmd.Parameters.AddWithValue("id", newId);
                cmd.Parameters.AddWithValue("oem", oem);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        _logger.LogInformation(
            "Manual create: minted fresh oitm {NewId} (supplier {Supplier}, ItemCode {Code}, {OemCount} OEM ref(s)).",
            newId, supplierName, sapItemCode, oemNumbers.Count);
        return newId;
    }

    public async Task<IReadOnlyList<string>> GetOemCrossReferencesAsync(long oitmId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        // OEM cross-references ONLY — never the 'iam_equivalent' aftermarket rows. Namespace guard:
        // exclude any token that is actually one of our internal SKUs (an item_code), so a leaked-SKU
        // cross-reference can't pollute the SAP ItemName with our own primary key.
        const string sql = """
            SELECT x.oem_number FROM oitm_cross_reference x
            WHERE x.oitm_id = @id AND x.reference_type = 'oem' AND x.oem_number IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM oitm o WHERE o.item_code = x.oem_number)
            ORDER BY x.oem_number;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", oitmId);

        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (!r.IsDBNull(0)) list.Add(r.GetString(0));
        return list;
    }
}
