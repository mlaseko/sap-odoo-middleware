using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Inventory;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Read-only SQL layer for the Autohub inventory app (MOLAS_Live_2021).
/// All reads go through direct SQL (fast, no DI API license seat); all writes
/// stay on the DI API via <see cref="IAutohubSapB1Service"/>.
/// </summary>
public interface IAutohubInventorySqlService
{
    /// <summary>All warehouses with their bin-activation flag (cached ~5 min).</summary>
    Task<List<WarehouseInfo>> GetWarehousesAsync(CancellationToken ct);

    /// <summary>True when the warehouse manages bin locations (OWHS.BinActivat = 'Y').</summary>
    Task<bool> IsBinManagedAsync(string whsCode, CancellationToken ct);

    /// <summary>
    /// Normalized stock for an item: OIBQ per-bin detail for bin warehouses,
    /// OITW on-hand for non-bin warehouses. One row per item-warehouse.
    /// </summary>
    Task<List<StockResponse>> GetStockAsync(string itemCode, string? whsCode, CancellationToken ct);

    /// <summary>Bins holding stock for an item in one warehouse, quantity descending (spec §8.2).</summary>
    Task<List<BinOption>> GetBinStockAsync(string itemCode, string whsCode, CancellationToken ct);

    /// <summary>Item's default bin in a warehouse (OITW.DftBinAbs), or null when unset.</summary>
    Task<int?> GetDefaultBinAsync(string itemCode, string whsCode, CancellationToken ct);

    /// <summary>BinCode for an OBIN AbsEntry, or null when the bin does not exist.</summary>
    Task<string?> GetBinCodeAsync(int binAbs, CancellationToken ct);

    /// <summary>BinCode + owning warehouse for an OBIN AbsEntry, or null when the bin does not exist.</summary>
    Task<(string BinCode, string WhsCode)?> GetBinInfoAsync(int binAbs, CancellationToken ct);

    /// <summary>All active bins of one warehouse (for the free destination picker), by BinCode.</summary>
    Task<List<WarehouseBin>> GetWarehouseBinsAsync(string whsCode, CancellationToken ct);

    /// <summary>
    /// The warehouse's SAP branch assignment (OWHS.BPLid joined to OBPL), or null when
    /// the warehouse has no branch configured. Required on service-created documents
    /// (Inventory Counting/Posting) when SAP's multi-branch feature is enabled.
    /// </summary>
    Task<WarehouseBranch?> GetWarehouseBranchAsync(string whsCode, CancellationToken ct);

    /// <summary>Open transfer request lines with item details (spec §8.1), oldest first.</summary>
    Task<List<OpenTransferRequestLine>> GetOpenTransferRequestsAsync(
        string? fromWhs, string? toWhs, CancellationToken ct);

    /// <summary>Open purchase order lines awaiting receipt (for the GRPO receiving screen), oldest first.</summary>
    Task<List<OpenPurchaseOrderLine>> GetOpenPurchaseOrderLinesAsync(
        string? cardCode, string? itemCode, CancellationToken ct);

    /// <summary>
    /// Idempotency probe: DocEntry/DocNum of an already-posted document carrying this U_AppRef
    /// GUID, or null. <paramref name="headerTable"/> must be one of OIGN/OWTQ/OWTR/OINC/OIQR.
    /// </summary>
    Task<(int DocEntry, int DocNum)?> FindDocEntryByAppRefAsync(
        string headerTable, string appRef, CancellationToken ct);

    /// <summary>
    /// Generates counting line seeds for a bin warehouse (spec §8.3): one line per
    /// item-per-bin with stock, scoped by a BinCode range or an explicit bin list.
    /// </summary>
    Task<List<CountingLineSeed>> GetBinCountingSeedsAsync(
        string whsCode, string? binFrom, string? binTo, List<int>? binAbsList, CancellationToken ct);

    /// <summary>Counting line seeds for the non-bin warehouse (01): one line per item with OITW stock.</summary>
    Task<List<CountingLineSeed>> GetNonBinCountingSeedsAsync(string whsCode, CancellationToken ct);

    /// <summary>Counting session headers with counted-line progress, newest first.</summary>
    Task<List<CountingSessionSummary>> GetCountingSessionsAsync(bool openOnly, CancellationToken ct);

    /// <summary>All lines of one counting session for the count-capture screen.</summary>
    Task<List<CountingLineDetail>> GetCountingLinesAsync(int docEntry, CancellationToken ct);

    /// <summary>Variance review lines (spec §8.4), sorted by absolute variance value descending.</summary>
    Task<List<CountingVarianceLine>> GetCountingVarianceAsync(int docEntry, CancellationToken ct);

    /// <summary>
    /// Default-bin seed candidates (spec §10): the top stocked non-system bin per
    /// item-warehouse, with the current OITW.DftBinAbs for skip/overwrite decisions.
    /// Ordered by ItemCode so a re-run processes items deterministically.
    /// </summary>
    Task<List<DefaultBinSeedRow>> GetDefaultBinSeedRowsAsync(CancellationToken ct);
}

/// <summary>
/// Direct MSSQL reads against the Autohub SAP company. Resolves the Autohub tenant config
/// directly (not ICompanyContext) so it can run as a singleton — same pattern as
/// <see cref="SapSkuCounterRefreshService"/>.
/// </summary>
public sealed class AutohubInventorySqlService : IAutohubInventorySqlService
{
    // U_AppRef idempotency probes are limited to the inventory header tables
    // (the five from spec §6.3 plus OPDN for GRPO).
    private static readonly HashSet<string> AllowedAppRefTables =
        new(StringComparer.OrdinalIgnoreCase) { "OIGN", "OWTQ", "OWTR", "OINC", "OIQR", "OPDN" };

    private static readonly TimeSpan WarehouseCacheTtl = TimeSpan.FromMinutes(5);

    private readonly CompaniesOptions _companies;
    private readonly ILogger<AutohubInventorySqlService> _logger;

    private List<WarehouseInfo>? _warehouseCache;
    private DateTime _warehouseCacheAt = DateTime.MinValue;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private bool _warnedMissingDbLogin;

    public AutohubInventorySqlService(
        IOptions<CompaniesOptions> companies,
        ILogger<AutohubInventorySqlService> logger)
    {
        _companies = companies.Value;
        _logger = logger;
    }

    // ── Warehouses ───────────────────────────────────────────────────

    public async Task<List<WarehouseInfo>> GetWarehousesAsync(CancellationToken ct)
    {
        if (_warehouseCache is not null && DateTime.UtcNow - _warehouseCacheAt < WarehouseCacheTtl)
            return _warehouseCache;

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_warehouseCache is not null && DateTime.UtcNow - _warehouseCacheAt < WarehouseCacheTtl)
                return _warehouseCache;

            const string sql = """
                SELECT WhsCode, WhsName, ISNULL(BinActivat, 'N')
                FROM OWHS
                ORDER BY WhsCode;
                """;

            var list = new List<WarehouseInfo>();
            await using var conn = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new WarehouseInfo
                {
                    WhsCode = reader.GetString(0),
                    WhsName = reader.GetString(1),
                    BinActivated = reader.GetString(2) == "Y",
                });
            }

            _warehouseCache = list;
            _warehouseCacheAt = DateTime.UtcNow;
            return list;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<bool> IsBinManagedAsync(string whsCode, CancellationToken ct)
    {
        var warehouses = await GetWarehousesAsync(ct);
        var whs = warehouses.FirstOrDefault(w =>
            string.Equals(w.WhsCode, whsCode, StringComparison.OrdinalIgnoreCase));
        if (whs is null)
            throw new InvalidOperationException($"Warehouse '{whsCode}' does not exist in SAP.");
        return whs.BinActivated;
    }

    // ── Stock ────────────────────────────────────────────────────────

    public async Task<List<StockResponse>> GetStockAsync(
        string itemCode, string? whsCode, CancellationToken ct)
    {
        var warehouses = await GetWarehousesAsync(ct);
        var binManaged = warehouses.Where(w => w.BinActivated)
            .Select(w => w.WhsCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Warehouse-level on-hand from OITW (works for bin and non-bin warehouses).
        const string whsSql = """
            SELECT W.WhsCode, W.OnHand, I.ItemName, I.U_Article_No, I.U_ItemManufacturer
            FROM OITW W
            JOIN OITM I ON I.ItemCode = W.ItemCode
            WHERE W.ItemCode = @item
              AND (@whs IS NULL OR W.WhsCode = @whs)
              AND W.OnHand <> 0
            ORDER BY W.WhsCode;
            """;

        var results = new List<StockResponse>();
        await using var conn = await OpenAsync(ct);

        await using (var cmd = new SqlCommand(whsSql, conn))
        {
            cmd.Parameters.AddWithValue("@item", itemCode);
            cmd.Parameters.Add("@whs", System.Data.SqlDbType.NVarChar, 8).Value =
                (object?)whsCode ?? DBNull.Value;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var whs = reader.GetString(0);
                results.Add(new StockResponse
                {
                    ItemCode = itemCode,
                    ItemName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ArticleNumber = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Manufacturer = reader.IsDBNull(4) ? null : reader.GetString(4),
                    WhsCode = whs,
                    BinManaged = binManaged.Contains(whs),
                    TotalOnHand = reader.GetDecimal(1),
                });
            }
        }

        // 2. Per-bin breakdown from OIBQ for the bin-managed warehouses (spec §8.2).
        const string binSql = """
            SELECT Q.WhsCode, Q.BinAbs, B.BinCode, Q.OnHandQty
            FROM OIBQ Q
            JOIN OBIN B ON B.AbsEntry = Q.BinAbs
            WHERE Q.ItemCode = @item
              AND (@whs IS NULL OR Q.WhsCode = @whs)
              AND Q.OnHandQty > 0
            ORDER BY Q.WhsCode, Q.OnHandQty DESC;
            """;

        await using (var cmd = new SqlCommand(binSql, conn))
        {
            cmd.Parameters.AddWithValue("@item", itemCode);
            cmd.Parameters.Add("@whs", System.Data.SqlDbType.NVarChar, 8).Value =
                (object?)whsCode ?? DBNull.Value;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var whs = reader.GetString(0);
                var row = results.FirstOrDefault(r =>
                    string.Equals(r.WhsCode, whs, StringComparison.OrdinalIgnoreCase));
                if (row is null) continue;   // bin stock without OITW row — shouldn't happen
                row.Bins.Add(new BinDetail
                {
                    BinAbs = reader.GetInt32(1),
                    BinCode = reader.GetString(2),
                    OnHandQty = reader.GetDecimal(3),
                });
            }
        }

        return results;
    }

    public async Task<List<BinOption>> GetBinStockAsync(
        string itemCode, string whsCode, CancellationToken ct)
    {
        const string sql = """
            SELECT Q.BinAbs, B.BinCode, Q.OnHandQty
            FROM OIBQ Q
            JOIN OBIN B ON B.AbsEntry = Q.BinAbs
            WHERE Q.ItemCode = @item AND Q.WhsCode = @whs AND Q.OnHandQty > 0
            ORDER BY Q.OnHandQty DESC;
            """;

        var list = new List<BinOption>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@item", itemCode);
        cmd.Parameters.AddWithValue("@whs", whsCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new BinOption
            {
                BinAbs = reader.GetInt32(0),
                BinCode = reader.GetString(1),
                OnHandQty = reader.GetDecimal(2),
            });
        }
        return list;
    }

    public async Task<int?> GetDefaultBinAsync(string itemCode, string whsCode, CancellationToken ct)
    {
        const string sql = """
            SELECT DftBinAbs FROM OITW
            WHERE ItemCode = @item AND WhsCode = @whs;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@item", itemCode);
        cmd.Parameters.AddWithValue("@whs", whsCode);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    public async Task<string?> GetBinCodeAsync(int binAbs, CancellationToken ct)
    {
        const string sql = "SELECT BinCode FROM OBIN WHERE AbsEntry = @abs;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@abs", binAbs);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (string)result;
    }

    public async Task<(string BinCode, string WhsCode)?> GetBinInfoAsync(int binAbs, CancellationToken ct)
    {
        const string sql = "SELECT BinCode, WhsCode FROM OBIN WHERE AbsEntry = @abs;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@abs", binAbs);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return (reader.GetString(0), reader.GetString(1));
    }

    public async Task<List<WarehouseBin>> GetWarehouseBinsAsync(string whsCode, CancellationToken ct)
    {
        const string sql = """
            SELECT AbsEntry, BinCode, ISNULL(SysBin, 'N'), ISNULL(Disabled, 'N')
            FROM OBIN
            WHERE WhsCode = @whs
            ORDER BY BinCode;
            """;

        var list = new List<WarehouseBin>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@whs", whsCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetString(3) == "Y")
                continue;   // disabled bins are not valid targets
            list.Add(new WarehouseBin
            {
                BinAbs = reader.GetInt32(0),
                BinCode = reader.GetString(1),
                SysBin = reader.GetString(2) == "Y",
            });
        }
        return list;
    }

    // ── Transfer requests ────────────────────────────────────────────

    public async Task<List<OpenTransferRequestLine>> GetOpenTransferRequestsAsync(
        string? fromWhs, string? toWhs, CancellationToken ct)
    {
        // Spec §8.1 + OITM item details. OWTQ.Filler genuinely is the
        // from-warehouse column — legacy SAP naming.
        const string sql = """
            SELECT T0.DocEntry, T0.DocNum, T0.DocDate,
                   T0.Filler AS FromWhs, T0.ToWhsCode AS ToWhs,
                   T1.LineNum, T1.ItemCode,
                   I.ItemName, I.U_Article_No, I.U_ItemManufacturer,
                   T1.Quantity, T1.OpenQty
            FROM OWTQ T0
            JOIN WTQ1 T1 ON T1.DocEntry = T0.DocEntry
            JOIN OITM I ON I.ItemCode = T1.ItemCode
            WHERE T0.DocStatus = 'O' AND T1.LineStatus = 'O'
              AND (@fromWhs IS NULL OR T0.Filler = @fromWhs)
              AND (@toWhs IS NULL OR T0.ToWhsCode = @toWhs)
            ORDER BY T0.DocDate, T0.DocNum, T1.LineNum;
            """;

        var list = new List<OpenTransferRequestLine>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@fromWhs", System.Data.SqlDbType.NVarChar, 8).Value =
            (object?)fromWhs ?? DBNull.Value;
        cmd.Parameters.Add("@toWhs", System.Data.SqlDbType.NVarChar, 8).Value =
            (object?)toWhs ?? DBNull.Value;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new OpenTransferRequestLine
            {
                DocEntry = reader.GetInt32(0),
                DocNum = reader.GetInt32(1),
                DocDate = reader.GetDateTime(2).ToString("yyyy-MM-dd"),
                FromWhs = reader.IsDBNull(3) ? "" : reader.GetString(3),
                ToWhs = reader.IsDBNull(4) ? "" : reader.GetString(4),
                LineNum = reader.GetInt32(5),
                ItemCode = reader.GetString(6),
                ItemName = reader.IsDBNull(7) ? null : reader.GetString(7),
                ArticleNumber = reader.IsDBNull(8) ? null : reader.GetString(8),
                Manufacturer = reader.IsDBNull(9) ? null : reader.GetString(9),
                Quantity = (double)reader.GetDecimal(10),
                OpenQty = (double)reader.GetDecimal(11),
            });
        }
        return list;
    }

    // ── Branch (Business Place) resolution ───────────────────────────

    public async Task<WarehouseBranch?> GetWarehouseBranchAsync(string whsCode, CancellationToken ct)
    {
        const string sql = """
            SELECT W.BPLid, B.BPLId, B.BPLName, ISNULL(B.Disabled, 'N')
            FROM OWHS W
            LEFT JOIN OBPL B ON B.BPLId = W.BPLid
            WHERE W.WhsCode = @whs;
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@whs", whsCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;                       // warehouse does not exist
        if (reader.IsDBNull(0))
            return null;                       // warehouse has no branch assigned

        bool branchRowExists = !reader.IsDBNull(1);
        return new WarehouseBranch
        {
            BplId = reader.GetInt32(0),
            BranchName = reader.IsDBNull(2) ? null : reader.GetString(2),
            // Active only when the OBPL row exists and is not disabled.
            Active = branchRowExists && reader.GetString(3) == "N",
        };
    }

    // ── Purchase orders (GRPO receiving screen) ──────────────────────

    public async Task<List<OpenPurchaseOrderLine>> GetOpenPurchaseOrderLinesAsync(
        string? cardCode, string? itemCode, CancellationToken ct)
    {
        const string sql = """
            SELECT T0.DocEntry, T0.DocNum, T0.DocDate, T0.CardCode, T0.CardName,
                   T1.LineNum, T1.ItemCode,
                   I.ItemName, I.U_Article_No, I.U_ItemManufacturer,
                   T1.Quantity, T1.OpenQty, T1.WhsCode
            FROM OPOR T0
            JOIN POR1 T1 ON T1.DocEntry = T0.DocEntry
            JOIN OITM I ON I.ItemCode = T1.ItemCode
            WHERE T0.DocStatus = 'O' AND T1.LineStatus = 'O' AND T1.OpenQty > 0
              AND (@card IS NULL OR T0.CardCode = @card)
              AND (@item IS NULL OR T1.ItemCode = @item)
            ORDER BY T0.DocDate, T0.DocNum, T1.LineNum;
            """;

        var list = new List<OpenPurchaseOrderLine>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@card", System.Data.SqlDbType.NVarChar, 15).Value =
            (object?)cardCode ?? DBNull.Value;
        cmd.Parameters.Add("@item", System.Data.SqlDbType.NVarChar, 50).Value =
            (object?)itemCode ?? DBNull.Value;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new OpenPurchaseOrderLine
            {
                DocEntry = reader.GetInt32(0),
                DocNum = reader.GetInt32(1),
                DocDate = reader.IsDBNull(2) ? "" : reader.GetDateTime(2).ToString("yyyy-MM-dd"),
                CardCode = reader.GetString(3),
                CardName = reader.IsDBNull(4) ? null : reader.GetString(4),
                LineNum = reader.GetInt32(5),
                ItemCode = reader.GetString(6),
                ItemName = reader.IsDBNull(7) ? null : reader.GetString(7),
                ArticleNumber = reader.IsDBNull(8) ? null : reader.GetString(8),
                Manufacturer = reader.IsDBNull(9) ? null : reader.GetString(9),
                Quantity = (double)reader.GetDecimal(10),
                OpenQty = (double)reader.GetDecimal(11),
                WhsCode = reader.IsDBNull(12) ? "" : reader.GetString(12),
            });
        }
        return list;
    }

    // ── Counting sessions ────────────────────────────────────────────

    public async Task<List<CountingLineSeed>> GetBinCountingSeedsAsync(
        string whsCode, string? binFrom, string? binTo, List<int>? binAbsList, CancellationToken ct)
    {
        // Spec §8.3: one line per item-per-bin with stock in scope.
        var sql = """
            SELECT Q.ItemCode, Q.WhsCode, Q.BinAbs
            FROM OIBQ Q
            JOIN OBIN B ON B.AbsEntry = Q.BinAbs
            WHERE Q.WhsCode = @whs
              AND Q.OnHandQty <> 0
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand();
        cmd.Connection = conn;
        cmd.Parameters.AddWithValue("@whs", whsCode);

        if (binAbsList is { Count: > 0 })
        {
            var names = new List<string>(binAbsList.Count);
            for (int i = 0; i < binAbsList.Count; i++)
            {
                names.Add($"@b{i}");
                cmd.Parameters.AddWithValue($"@b{i}", binAbsList[i]);
            }
            sql += $"\n  AND Q.BinAbs IN ({string.Join(", ", names)})";
        }
        else
        {
            sql += "\n  AND B.BinCode BETWEEN @binFrom AND @binTo";
            cmd.Parameters.AddWithValue("@binFrom", binFrom ?? "");
            cmd.Parameters.AddWithValue("@binTo", binTo ?? "");
        }

        sql += "\nORDER BY B.BinCode, Q.ItemCode;";
        cmd.CommandText = sql;

        var list = new List<CountingLineSeed>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new CountingLineSeed
            {
                ItemCode = reader.GetString(0),
                WhsCode = reader.GetString(1),
                BinEntry = reader.GetInt32(2),
            });
        }
        return list;
    }

    public async Task<List<CountingLineSeed>> GetNonBinCountingSeedsAsync(
        string whsCode, CancellationToken ct)
    {
        const string sql = """
            SELECT W.ItemCode, W.WhsCode
            FROM OITW W
            WHERE W.WhsCode = @whs AND W.OnHand <> 0
            ORDER BY W.ItemCode;
            """;

        var list = new List<CountingLineSeed>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@whs", whsCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new CountingLineSeed
            {
                ItemCode = reader.GetString(0),
                WhsCode = reader.GetString(1),
                BinEntry = null,
            });
        }
        return list;
    }

    public async Task<List<CountingSessionSummary>> GetCountingSessionsAsync(
        bool openOnly, CancellationToken ct)
    {
        const string sql = """
            SELECT H.DocEntry, H.DocNum, H.CountDate, H.Status,
                   MIN(L.WhsCode) AS WhsCode,
                   COUNT(*) AS TotalLines,
                   SUM(CASE WHEN L.Counted = 'Y' THEN 1 ELSE 0 END) AS CountedLines
            FROM OINC H
            JOIN INC1 L ON L.DocEntry = H.DocEntry
            WHERE (@openOnly = 0 OR H.Status = 'O')
            GROUP BY H.DocEntry, H.DocNum, H.CountDate, H.Status
            ORDER BY H.DocEntry DESC;
            """;

        var list = new List<CountingSessionSummary>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@openOnly", openOnly ? 1 : 0);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new CountingSessionSummary
            {
                DocEntry = reader.GetInt32(0),
                DocNum = reader.GetInt32(1),
                CountDate = reader.IsDBNull(2) ? "" : reader.GetDateTime(2).ToString("yyyy-MM-dd"),
                Status = reader.IsDBNull(3) ? "" : reader.GetString(3),
                WhsCode = reader.IsDBNull(4) ? "" : reader.GetString(4),
                TotalLines = reader.GetInt32(5),
                CountedLines = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
            });
        }
        return list;
    }

    public async Task<List<CountingLineDetail>> GetCountingLinesAsync(
        int docEntry, CancellationToken ct)
    {
        // INC1.InWhsQty / CountQty are the standard schema (spec §8.4 caveat: verify
        // with SELECT TOP 1 * FROM INC1 against this SAP version if either errors).
        const string sql = """
            SELECT L.LineNum, L.ItemCode, I.ItemName, I.U_Article_No, I.U_ItemManufacturer,
                   L.WhsCode, L.BinEntry, B.BinCode,
                   L.InWhsQty, L.CountQty, L.Counted, L.LineStatus
            FROM INC1 L
            JOIN OITM I ON I.ItemCode = L.ItemCode
            LEFT JOIN OBIN B ON B.AbsEntry = L.BinEntry
            WHERE L.DocEntry = @doc
            ORDER BY B.BinCode, L.ItemCode;
            """;

        var list = new List<CountingLineDetail>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@doc", docEntry);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new CountingLineDetail
            {
                LineNum = reader.GetInt32(0),
                ItemCode = reader.GetString(1),
                ItemName = reader.IsDBNull(2) ? null : reader.GetString(2),
                ArticleNumber = reader.IsDBNull(3) ? null : reader.GetString(3),
                Manufacturer = reader.IsDBNull(4) ? null : reader.GetString(4),
                WhsCode = reader.IsDBNull(5) ? "" : reader.GetString(5),
                BinEntry = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                BinCode = reader.IsDBNull(7) ? null : reader.GetString(7),
                SystemQty = reader.IsDBNull(8) ? 0m : reader.GetDecimal(8),
                CountedQty = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                Counted = !reader.IsDBNull(10) && reader.GetString(10) == "Y",
                LineStatus = reader.IsDBNull(11) ? "" : reader.GetString(11),
            });
        }
        return list;
    }

    public async Task<List<CountingVarianceLine>> GetCountingVarianceAsync(
        int docEntry, CancellationToken ct)
    {
        // Spec §8.4 — most expensive discrepancies first.
        const string sql = """
            SELECT L.LineNum, L.ItemCode, I.ItemName, I.U_Article_No, I.U_ItemManufacturer,
                   L.WhsCode, B.BinCode,
                   L.InWhsQty, L.CountQty,
                   ISNULL(L.CountQty, 0) - L.InWhsQty AS VarianceQty,
                   (ISNULL(L.CountQty, 0) - L.InWhsQty) * ISNULL(W.AvgPrice, 0) AS VarianceValue,
                   L.Counted, L.LineStatus
            FROM INC1 L
            JOIN OITM I ON I.ItemCode = L.ItemCode
            JOIN OITW W ON W.ItemCode = L.ItemCode AND W.WhsCode = L.WhsCode
            LEFT JOIN OBIN B ON B.AbsEntry = L.BinEntry
            WHERE L.DocEntry = @doc
            ORDER BY ABS((ISNULL(L.CountQty, 0) - L.InWhsQty) * ISNULL(W.AvgPrice, 0)) DESC;
            """;

        var list = new List<CountingVarianceLine>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@doc", docEntry);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new CountingVarianceLine
            {
                LineNum = reader.GetInt32(0),
                ItemCode = reader.GetString(1),
                ItemName = reader.IsDBNull(2) ? null : reader.GetString(2),
                ArticleNumber = reader.IsDBNull(3) ? null : reader.GetString(3),
                Manufacturer = reader.IsDBNull(4) ? null : reader.GetString(4),
                WhsCode = reader.IsDBNull(5) ? "" : reader.GetString(5),
                BinCode = reader.IsDBNull(6) ? null : reader.GetString(6),
                SystemQty = reader.IsDBNull(7) ? 0m : reader.GetDecimal(7),
                CountedQty = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                VarianceQty = reader.IsDBNull(9) ? 0m : reader.GetDecimal(9),
                VarianceValue = reader.IsDBNull(10) ? 0m : reader.GetDecimal(10),
                Counted = !reader.IsDBNull(11) && reader.GetString(11) == "Y",
                LineStatus = reader.IsDBNull(12) ? "" : reader.GetString(12),
            });
        }
        return list;
    }

    // ── Default-bin seeding (spec §10) ───────────────────────────────

    public async Task<List<DefaultBinSeedRow>> GetDefaultBinSeedRowsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT x.ItemCode, x.WhsCode, x.BinAbs, W.DftBinAbs
            FROM (
              SELECT Q.ItemCode, Q.WhsCode, Q.BinAbs,
                     ROW_NUMBER() OVER (PARTITION BY Q.ItemCode, Q.WhsCode
                                        ORDER BY Q.OnHandQty DESC) AS rn
              FROM OIBQ Q
              JOIN OBIN B ON B.AbsEntry = Q.BinAbs
              WHERE Q.OnHandQty > 0 AND B.SysBin = 'N'
            ) x
            JOIN OITW W ON W.ItemCode = x.ItemCode AND W.WhsCode = x.WhsCode
            WHERE x.rn = 1
            ORDER BY x.ItemCode, x.WhsCode;
            """;

        var list = new List<DefaultBinSeedRow>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new DefaultBinSeedRow
            {
                ItemCode = reader.GetString(0),
                WhsCode = reader.GetString(1),
                BinAbs = reader.GetInt32(2),
                CurrentDftBinAbs = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            });
        }
        return list;
    }

    // ── Idempotency ──────────────────────────────────────────────────

    public async Task<(int DocEntry, int DocNum)?> FindDocEntryByAppRefAsync(
        string headerTable, string appRef, CancellationToken ct)
    {
        if (!AllowedAppRefTables.Contains(headerTable))
            throw new ArgumentException(
                $"Table '{headerTable}' is not an inventory header table (OIGN/OWTQ/OWTR/OINC/OIQR/OPDN).",
                nameof(headerTable));

        // Table name is whitelisted above — safe to interpolate.
        var sql = $"SELECT DocEntry, DocNum FROM {headerTable.ToUpperInvariant()} WHERE U_AppRef = @appRef;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@appRef", appRef);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    // ── Connection plumbing ──────────────────────────────────────────

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(ResolveConnectionString());
        await conn.OpenAsync(ct);
        return conn;
    }

    private string ResolveConnectionString()
    {
        if (!_companies.Companies.TryGetValue(CompanyContext.AutohubKey, out var cfg)
            || cfg.SapB1 is null
            || string.IsNullOrWhiteSpace(cfg.SapB1.Server)
            || string.IsNullOrWhiteSpace(cfg.SapB1.CompanyDb))
        {
            throw new InvalidOperationException(
                "Companies:Autohub:SapB1 (Server/CompanyDb) is not configured — required for inventory reads.");
        }

        if (!cfg.SapB1.DbServerType.Contains("MSSQL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Inventory reads support MSSQL only (DbServerType={cfg.SapB1.DbServerType}).");

        if (string.IsNullOrWhiteSpace(cfg.SapB1.DbUserName) && !_warnedMissingDbLogin)
        {
            _warnedMissingDbLogin = true;
            _logger.LogWarning(
                "Companies:Autohub:SapB1:DbUserName is not set — falling back to the DI API user " +
                "'{UserName}', which usually is NOT a SQL Server login. If SQL Server rejects the " +
                "login (error 18456), set DbUserName/DbPassword to a real SQL login in the external " +
                "appsettings.Production.json.",
                cfg.SapB1.UserName);
        }

        return BuildSapConnectionString(cfg.SapB1);
    }

    private static string BuildSapConnectionString(SapB1Settings sap) =>
        new SqlConnectionStringBuilder
        {
            DataSource = sap.Server,
            InitialCatalog = sap.CompanyDb,
            // Direct SQL needs a real SQL login (DbUserName), NOT the DI API's SAP B1 application user.
            UserID = string.IsNullOrWhiteSpace(sap.DbUserName) ? sap.UserName : sap.DbUserName,
            Password = string.IsNullOrWhiteSpace(sap.DbUserName) ? sap.Password : sap.DbPassword,
            TrustServerCertificate = true,
        }.ConnectionString;
}
