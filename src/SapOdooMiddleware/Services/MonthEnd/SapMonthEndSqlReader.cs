using System.Data;
using Microsoft.Data.SqlClient;
using SapOdooMiddleware.Models.MonthEnd;
using SapOdooMiddleware.Services.PurchaseSync;

namespace SapOdooMiddleware.Services.MonthEnd;

/// <summary>Reads the month-end figures of spec v4 §7 from the SAP company database. Direct SQL, read-only.</summary>
public interface IMonthEndReader
{
    Task<List<SupplierBalanceRow>> ReadSupplierBalancesAsync(DateOnly asOf, CancellationToken ct);
    Task<List<GrniRow>> ReadGrniAsync(DateOnly asOf, CancellationToken ct);
    Task<List<WarehouseValueRow>> ReadInventoryValuationAsync(DateOnly asOf, CancellationToken ct);
    Task<List<LandedCostCodeRow>> ReadLandedCostCreditsAsync(DateOnly from, DateOnly to, CancellationToken ct);
    Task<List<LandedCostDocumentRow>> ReadLandedCostDocumentsAsync(DateOnly from, DateOnly to, CancellationToken ct);
}

/// <summary>
/// SQL over JDT1/OCRD (supplier balances), OPDN/PDN1 with PCH1 and RPD1 (GRNI), OINM (perpetual inventory value)
/// and OALC/JDT1/OIPF (landed cost credits). Everything is parameterised. The UDF columns (OPDN.U_ShipRef,
/// OIPF.U_OdooShipmentId) are probed once: before the UDF routine (spec §4.1) has run they are read as NULL.
/// </summary>
public sealed class SapMonthEndSqlReader : IMonthEndReader
{
    private readonly SapSqlConnectionFactory _connections;
    private readonly ILogger<SapMonthEndSqlReader> _logger;

    private static bool? _opdnHasShipRef;
    private static bool? _oipfHasOdooShipmentId;

    public SapMonthEndSqlReader(SapSqlConnectionFactory connections, ILogger<SapMonthEndSqlReader> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Supplier balances (JDT1, credit-positive)
    // ------------------------------------------------------------------

    public async Task<List<SupplierBalanceRow>> ReadSupplierBalancesAsync(DateOnly asOf, CancellationToken ct)
    {
        const string sql = """
            SELECT T0.ShortName, C.CardName, ISNULL(T0.FCCurrency, '') AS FcCurrency,
                   SUM(T0.Credit - T0.Debit) AS BalanceLc,
                   SUM(T0.FCCredit - T0.FCDebit) AS BalanceFc
            FROM dbo.JDT1 T0
            JOIN dbo.OCRD C ON C.CardCode = T0.ShortName
            WHERE C.CardType = 'S' AND T0.RefDate <= @AsOf
            GROUP BY T0.ShortName, C.CardName, ISNULL(T0.FCCurrency, '')
            HAVING ABS(SUM(T0.Credit - T0.Debit)) > 0.005 OR ABS(SUM(T0.FCCredit - T0.FCDebit)) > 0.005
            ORDER BY T0.ShortName, FcCurrency
            """;

        await using var conn = await _connections.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@AsOf", SqlDbType.DateTime).Value = asOf.ToDateTime(TimeOnly.MinValue);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<SupplierBalanceRow>();
        while (await r.ReadAsync(ct))
        {
            rows.Add(new SupplierBalanceRow
            {
                CardCode = r.GetString(0).Trim(),
                CardName = r.IsDBNull(1) ? string.Empty : r.GetString(1).Trim(),
                Currency = r.GetString(2).Trim(),
                BalanceLc = Math.Round(Convert.ToDouble(r.GetValue(3)), 2),
                BalanceFc = Math.Round(Convert.ToDouble(r.GetValue(4)), 2),
            });
        }
        return rows;
    }

    // ------------------------------------------------------------------
    // GRNI: goods received at the date, not invoiced or returned at the date
    // ------------------------------------------------------------------

    public async Task<List<GrniRow>> ReadGrniAsync(DateOnly asOf, CancellationToken ct)
    {
        await using var conn = await _connections.OpenAsync(ct);
        await ProbeColumnsAsync(conn, ct);

        string shipRef = _opdnHasShipRef == true ? "H.U_ShipRef" : "CAST(NULL AS NVARCHAR(50))";
        string sql = $"""
            SELECT H.DocEntry, H.DocNum, H.CardCode, H.CardName, H.DocDate, {shipRef} AS ShipRef,
                   L.LineNum, L.Quantity, L.LineTotal,
                   ISNULL((SELECT SUM(P.Quantity) FROM dbo.PCH1 P JOIN dbo.OPCH PH ON PH.DocEntry = P.DocEntry
                           WHERE P.BaseType = 20 AND P.BaseEntry = L.DocEntry AND P.BaseLine = L.LineNum
                             AND PH.CANCELED = 'N' AND PH.DocDate <= @AsOf), 0) AS InvoicedQty,
                   ISNULL((SELECT SUM(R.Quantity) FROM dbo.RPD1 R JOIN dbo.ORPD RH ON RH.DocEntry = R.DocEntry
                           WHERE R.BaseType = 20 AND R.BaseEntry = L.DocEntry AND R.BaseLine = L.LineNum
                             AND RH.CANCELED = 'N' AND RH.DocDate <= @AsOf), 0) AS ReturnedQty
            FROM dbo.OPDN H
            JOIN dbo.PDN1 L ON L.DocEntry = H.DocEntry
            WHERE H.CANCELED = 'N' AND H.DocDate <= @AsOf AND L.Quantity <> 0
            ORDER BY H.DocEntry, L.LineNum
            """;

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        cmd.Parameters.Add("@AsOf", SqlDbType.DateTime).Value = asOf.ToDateTime(TimeOnly.MinValue);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        var byDoc = new Dictionary<int, GrniRow>();
        while (await r.ReadAsync(ct))
        {
            double received = Convert.ToDouble(r.GetValue(7));
            double lineTotal = Convert.ToDouble(r.GetValue(8));
            double invoiced = Convert.ToDouble(r.GetValue(9));
            double returned = Convert.ToDouble(r.GetValue(10));
            double openValue = MonthEndMath.OpenValue(received, lineTotal, invoiced, returned);
            if (openValue == 0) continue;

            int docEntry = r.GetInt32(0);
            if (!byDoc.TryGetValue(docEntry, out var row))
            {
                row = new GrniRow
                {
                    DocEntry = docEntry,
                    DocNum = r.GetInt32(1),
                    CardCode = r.GetString(2).Trim(),
                    CardName = r.IsDBNull(3) ? string.Empty : r.GetString(3).Trim(),
                    DocDate = DateOnly.FromDateTime(r.GetDateTime(4)),
                    ShipRef = r.IsDBNull(5) ? null : r.GetString(5).Trim(),
                };
                byDoc[docEntry] = row;
            }
            row.OpenLines++;
            row.OpenValueLc = Math.Round(row.OpenValueLc + openValue, 2);
        }
        return byDoc.Values.OrderBy(d => d.DocEntry).ToList();
    }

    // ------------------------------------------------------------------
    // Inventory value (OINM, perpetual)
    // ------------------------------------------------------------------

    public async Task<List<WarehouseValueRow>> ReadInventoryValuationAsync(DateOnly asOf, CancellationToken ct)
    {
        const string sql = """
            SELECT ISNULL(T0.Warehouse, '') AS Warehouse, SUM(T0.TransValue) AS ValueLc
            FROM dbo.OINM T0
            WHERE T0.DocDate <= @AsOf
            GROUP BY ISNULL(T0.Warehouse, '')
            ORDER BY Warehouse
            """;

        await using var conn = await _connections.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        cmd.Parameters.Add("@AsOf", SqlDbType.DateTime).Value = asOf.ToDateTime(TimeOnly.MinValue);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<WarehouseValueRow>();
        while (await r.ReadAsync(ct))
        {
            rows.Add(new WarehouseValueRow
            {
                Warehouse = r.GetString(0).Trim(),
                ValueLc = Math.Round(Convert.ToDouble(r.GetValue(1)), 2),
            });
        }
        return rows;
    }

    // ------------------------------------------------------------------
    // Landed cost credits per code (OALC allocation accounts, JDT1 transaction type 69)
    // ------------------------------------------------------------------

    public async Task<List<LandedCostCodeRow>> ReadLandedCostCreditsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string sql = """
            SELECT A.AlcCode, ISNULL(A.AlcName, '') AS AlcName, ISNULL(A.AcctCode, '') AS AcctCode,
                   ISNULL(SUM(J.Credit - J.Debit), 0) AS CreditsLc,
                   COUNT(DISTINCT J.TransId) AS Entries
            FROM dbo.OALC A
            LEFT JOIN dbo.JDT1 J ON J.Account = A.AcctCode AND J.TransType = '69'
                                 AND J.RefDate >= @From AND J.RefDate <= @To
            GROUP BY A.AlcCode, ISNULL(A.AlcName, ''), ISNULL(A.AcctCode, '')
            ORDER BY A.AlcCode
            """;

        await using var conn = await _connections.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@From", SqlDbType.DateTime).Value = from.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@To", SqlDbType.DateTime).Value = to.ToDateTime(TimeOnly.MinValue);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<LandedCostCodeRow>();
        while (await r.ReadAsync(ct))
        {
            rows.Add(new LandedCostCodeRow
            {
                LcCode = r.GetString(0).Trim(),
                Name = r.GetString(1).Trim(),
                AccountCode = r.GetString(2).Trim(),
                CreditsLc = Math.Round(Convert.ToDouble(r.GetValue(3)), 2),
                Entries = r.GetInt32(4),
            });
        }
        return rows;
    }

    public async Task<List<LandedCostDocumentRow>> ReadLandedCostDocumentsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = await _connections.OpenAsync(ct);
        await ProbeColumnsAsync(conn, ct);

        string odooRef = _oipfHasOdooShipmentId == true ? "H.U_OdooShipmentId" : "CAST(NULL AS NVARCHAR(50))";
        string sql = $"""
            SELECT H.DocEntry, H.DocNum, H.DocDate, {odooRef} AS OdooShipmentId,
                   ISNULL((SELECT SUM(J.Credit - J.Debit) FROM dbo.JDT1 J
                           WHERE J.TransId = H.TransId AND J.Account IN (SELECT A.AcctCode FROM dbo.OALC A)), 0) AS TotalLc
            FROM dbo.OIPF H
            WHERE H.DocDate >= @From AND H.DocDate <= @To
            ORDER BY H.DocEntry
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@From", SqlDbType.DateTime).Value = from.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@To", SqlDbType.DateTime).Value = to.ToDateTime(TimeOnly.MinValue);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<LandedCostDocumentRow>();
        while (await r.ReadAsync(ct))
        {
            rows.Add(new LandedCostDocumentRow
            {
                DocEntry = r.GetInt32(0),
                DocNum = r.GetInt32(1),
                DocDate = DateOnly.FromDateTime(r.GetDateTime(2)),
                OdooShipmentId = r.IsDBNull(3) ? null : r.GetString(3).Trim(),
                TotalLc = Math.Round(Convert.ToDouble(r.GetValue(4)), 2),
            });
        }
        return rows;
    }

    // ------------------------------------------------------------------

    private async Task ProbeColumnsAsync(SqlConnection conn, CancellationToken ct)
    {
        if (_opdnHasShipRef is not null && _oipfHasOdooShipmentId is not null) return;
        const string sql = """
            SELECT CASE WHEN COL_LENGTH('dbo.OPDN', 'U_ShipRef') IS NULL THEN 0 ELSE 1 END,
                   CASE WHEN COL_LENGTH('dbo.OIPF', 'U_OdooShipmentId') IS NULL THEN 0 ELSE 1 END
            """;
        await using var cmd = new SqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            _opdnHasShipRef = r.GetInt32(0) == 1;
            _oipfHasOdooShipmentId = r.GetInt32(1) == 1;
            if (_opdnHasShipRef == false || _oipfHasOdooShipmentId == false)
                _logger.LogWarning("Month-end reads: UDF columns missing (OPDN.U_ShipRef={A}, OIPF.U_OdooShipmentId={B}); read as NULL until POST /api/admin/setup-sap-udfs runs.",
                    _opdnHasShipRef, _oipfHasOdooShipmentId);
        }
    }
}
