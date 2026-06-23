using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.Reports;

/// <summary>One row of the Lubes inventory coverage/forecast report (matches the report SQL columns).</summary>
public sealed record LubesInventoryForecastRow(
    string ItemCode,
    string? ItemName,
    string? ItemGroup,
    decimal OnHandQty,
    decimal OrderedQty,
    decimal OnHandPlusOrderedQty,
    decimal InventoryOutLast3Months,
    decimal AvgMonthlyOut3M,
    decimal? CoverageMonths3M,
    decimal InventoryOutLast6Months,
    decimal AvgMonthlyOut6M,
    decimal? CoverageMonths6M,
    DateTime? LastInventoryOutDate,
    string CoverageStatus);

public interface ILubesInventoryForecastService
{
    /// <summary>
    /// Runs the inventory coverage/forecast report against the Molas <b>Lubes</b> SAP B1 database
    /// (the default top-level <c>SapB1</c> connection) and returns one row per stock item.
    /// </summary>
    Task<IReadOnlyList<LubesInventoryForecastRow>> GetForecastAsync(CancellationToken ct);
}

/// <summary>
/// Read-only direct-SQL report over the Lubes SAP company. Mirrors the connection-string approach used
/// by the Autohub SKU refresh (a real SQL login via <see cref="SapB1Settings.DbUserName"/>, falling back
/// to the DI-API user when the SAP B1 user IS the SQL login). The report is computed entirely server-side
/// (OnHand from OITW, open POs from OPOR/POR1, outflow from OINM over rolling 3/6-month windows) so the
/// middleware just streams the rows back. Uses <see cref="IOptions{SapB1Settings}"/> = the Lubes tenant;
/// it never touches the Autohub connection.
/// </summary>
public sealed class LubesInventoryForecastService : ILubesInventoryForecastService
{
    private readonly SapB1Settings _sap;
    private readonly ILogger<LubesInventoryForecastService> _logger;

    public LubesInventoryForecastService(IOptions<SapB1Settings> sap, ILogger<LubesInventoryForecastService> logger)
    {
        _sap = sap.Value;
        _logger = logger;
    }

    // Verbatim report query (T-SQL / MSSQL). All windows are relative to GETDATE() on the SQL server.
    private const string ReportSql = """
        WITH ItemMaster AS (
            SELECT T0.ItemCode, T0.ItemName, T1.ItmsGrpNam AS ItemGroup
            FROM OITM T0
            LEFT JOIN OITB T1 ON T0.ItmsGrpCod = T1.ItmsGrpCod
            WHERE T0.InvntItem = 'Y' AND T0.validFor = 'Y' AND T0.frozenFor = 'N'
        ),
        OnHand AS (
            SELECT ItemCode, SUM(OnHand) AS OnHandQty
            FROM OITW
            GROUP BY ItemCode
        ),
        OpenPO AS (
            SELECT T1.ItemCode, SUM(T1.OpenQty) AS OrderedQty
            FROM OPOR T0
            INNER JOIN POR1 T1 ON T0.DocEntry = T1.DocEntry
            WHERE T0.CANCELED = 'N' AND T0.DocStatus = 'O' AND T1.LineStatus = 'O' AND T1.OpenQty > 0
            GROUP BY T1.ItemCode
        ),
        InventoryOut AS (
            SELECT
                ItemCode,
                SUM(CASE WHEN DocDate >= DATEADD(MONTH, -3, CAST(GETDATE() AS DATE)) THEN OutQty ELSE 0 END) AS InventoryOut_Last3Months,
                SUM(CASE WHEN DocDate >= DATEADD(MONTH, -6, CAST(GETDATE() AS DATE)) THEN OutQty ELSE 0 END) AS InventoryOut_Last6Months,
                MAX(CASE WHEN OutQty > 0 THEN DocDate ELSE NULL END) AS LastInventoryOutDate
            FROM OINM
            WHERE OutQty > 0
              AND TransType <> 67
              AND DocDate >= DATEADD(MONTH, -6, CAST(GETDATE() AS DATE))
            GROUP BY ItemCode
        )
        SELECT
            M.ItemCode,
            M.ItemName,
            M.ItemGroup,
            ISNULL(H.OnHandQty, 0) AS OnHandQty,
            ISNULL(P.OrderedQty, 0) AS OrderedQty,
            ISNULL(H.OnHandQty, 0) + ISNULL(P.OrderedQty, 0) AS OnHandPlusOrderedQty,
            ISNULL(O.InventoryOut_Last3Months, 0) AS InventoryOut_Last3Months,
            CAST(ISNULL(O.InventoryOut_Last3Months, 0) / 3.0 AS DECIMAL(19, 2)) AS AvgMonthlyOut_3M,
            CASE WHEN ISNULL(O.InventoryOut_Last3Months, 0) = 0 THEN NULL
                 ELSE CAST((ISNULL(H.OnHandQty, 0) + ISNULL(P.OrderedQty, 0))
                      / NULLIF((ISNULL(O.InventoryOut_Last3Months, 0) / 3.0), 0) AS DECIMAL(19, 2)) END AS CoverageMonths_3M,
            ISNULL(O.InventoryOut_Last6Months, 0) AS InventoryOut_Last6Months,
            CAST(ISNULL(O.InventoryOut_Last6Months, 0) / 6.0 AS DECIMAL(19, 2)) AS AvgMonthlyOut_6M,
            CASE WHEN ISNULL(O.InventoryOut_Last6Months, 0) = 0 THEN NULL
                 ELSE CAST((ISNULL(H.OnHandQty, 0) + ISNULL(P.OrderedQty, 0))
                      / NULLIF((ISNULL(O.InventoryOut_Last6Months, 0) / 6.0), 0) AS DECIMAL(19, 2)) END AS CoverageMonths_6M,
            O.LastInventoryOutDate,
            CASE
                WHEN ISNULL(O.InventoryOut_Last6Months, 0) = 0 THEN 'No Movement'
                WHEN ((ISNULL(H.OnHandQty, 0) + ISNULL(P.OrderedQty, 0)) / NULLIF((ISNULL(O.InventoryOut_Last6Months, 0) / 6.0), 0)) < 1 THEN 'Under 1 Month Cover'
                WHEN ((ISNULL(H.OnHandQty, 0) + ISNULL(P.OrderedQty, 0)) / NULLIF((ISNULL(O.InventoryOut_Last6Months, 0) / 6.0), 0)) < 3 THEN '1 to 3 Months Cover'
                WHEN ((ISNULL(H.OnHandQty, 0) + ISNULL(P.OrderedQty, 0)) / NULLIF((ISNULL(O.InventoryOut_Last6Months, 0) / 6.0), 0)) < 6 THEN '3 to 6 Months Cover'
                ELSE 'Over 6 Months Cover'
            END AS CoverageStatus
        FROM ItemMaster M
        LEFT JOIN OnHand H ON M.ItemCode = H.ItemCode
        LEFT JOIN OpenPO P ON M.ItemCode = P.ItemCode
        LEFT JOIN InventoryOut O ON M.ItemCode = O.ItemCode
        ORDER BY M.ItemCode;
        """;

    public async Task<IReadOnlyList<LubesInventoryForecastRow>> GetForecastAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_sap.Server) || string.IsNullOrWhiteSpace(_sap.CompanyDb))
            throw new InvalidOperationException("Lubes SapB1 connection (Server/CompanyDb) is not configured.");
        if (!_sap.DbServerType.Contains("MSSQL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Inventory forecast is MSSQL-only (DbServerType={_sap.DbServerType}).");

        await using var conn = new SqlConnection(BuildConnectionString(_sap));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(ReportSql, conn) { CommandTimeout = 120 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        int iItemCode = reader.GetOrdinal("ItemCode");
        int iItemName = reader.GetOrdinal("ItemName");
        int iItemGroup = reader.GetOrdinal("ItemGroup");
        int iOnHand = reader.GetOrdinal("OnHandQty");
        int iOrdered = reader.GetOrdinal("OrderedQty");
        int iSum = reader.GetOrdinal("OnHandPlusOrderedQty");
        int iOut3 = reader.GetOrdinal("InventoryOut_Last3Months");
        int iAvg3 = reader.GetOrdinal("AvgMonthlyOut_3M");
        int iCov3 = reader.GetOrdinal("CoverageMonths_3M");
        int iOut6 = reader.GetOrdinal("InventoryOut_Last6Months");
        int iAvg6 = reader.GetOrdinal("AvgMonthlyOut_6M");
        int iCov6 = reader.GetOrdinal("CoverageMonths_6M");
        int iLast = reader.GetOrdinal("LastInventoryOutDate");
        int iStatus = reader.GetOrdinal("CoverageStatus");

        var rows = new List<LubesInventoryForecastRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new LubesInventoryForecastRow(
                ItemCode: reader.GetString(iItemCode),
                ItemName: Str(reader, iItemName),
                ItemGroup: Str(reader, iItemGroup),
                OnHandQty: Dec(reader, iOnHand),
                OrderedQty: Dec(reader, iOrdered),
                OnHandPlusOrderedQty: Dec(reader, iSum),
                InventoryOutLast3Months: Dec(reader, iOut3),
                AvgMonthlyOut3M: Dec(reader, iAvg3),
                CoverageMonths3M: NullableDec(reader, iCov3),
                InventoryOutLast6Months: Dec(reader, iOut6),
                AvgMonthlyOut6M: Dec(reader, iAvg6),
                CoverageMonths6M: NullableDec(reader, iCov6),
                LastInventoryOutDate: reader.IsDBNull(iLast) ? null : reader.GetDateTime(iLast),
                CoverageStatus: reader.GetString(iStatus)));
        }

        _logger.LogInformation("Lubes inventory forecast: returned {Count} item(s).", rows.Count);
        return rows;
    }

    private static string? Str(IDataRecord r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static decimal Dec(IDataRecord r, int i) => r.IsDBNull(i) ? 0m : Convert.ToDecimal(r.GetValue(i));
    private static decimal? NullableDec(IDataRecord r, int i) => r.IsDBNull(i) ? null : Convert.ToDecimal(r.GetValue(i));

    private static string BuildConnectionString(SapB1Settings sap) =>
        new SqlConnectionStringBuilder
        {
            DataSource = sap.Server,
            InitialCatalog = sap.CompanyDb,
            // Direct SQL needs a real SQL login (DbUserName); fall back to the DI-API user when it IS the SQL login.
            UserID = string.IsNullOrWhiteSpace(sap.DbUserName) ? sap.UserName : sap.DbUserName,
            Password = string.IsNullOrWhiteSpace(sap.DbUserName) ? sap.Password : sap.DbPassword,
            TrustServerCertificate = true,
        }.ConnectionString;
}
