using System.Data;
using Microsoft.Data.SqlClient;
using SapOdooMiddleware.Models.PurchaseSync;

namespace SapOdooMiddleware.Services.PurchaseSync;

/// <summary>Reads the SAP purchasing documents the sync needs. Direct SQL, read-only.</summary>
public interface ISapPurchaseReader
{
    Task<SapPoHeader?> ReadPurchaseOrderAsync(int docEntry, CancellationToken ct);
    Task<SapGrpoHeader?> ReadGrpoAsync(int docEntry, CancellationToken ct);

    /// <param name="objType">18 = A/P Invoice (OPCH), 19 = A/P Credit Memo (ORPC).</param>
    Task<SapApDocument?> ReadApDocumentAsync(int objType, int docEntry, CancellationToken ct);

    /// <summary>
    /// Documents dated on or after <paramref name="fromDate"/> for the backfill (spec v4 §6.5):
    /// open POs, GRPOs, A/P invoices and credit memos, cancellation documents excluded.
    /// </summary>
    Task<List<SapBackfillCandidate>> ListBackfillCandidatesAsync(
        DateTime fromDate, IReadOnlyCollection<string> objectTypes, CancellationToken ct);
}

/// <summary>
/// SQL over OPOR/POR1, OPDN/PDN1, OPCH/PCH1/PCH3 and ORPC/RPC1/RPC3 on the Lubes company database.
/// Everything is parameterised. The OPDN UDF columns (U_ShipRef, U_OdooShipmentId) are probed once:
/// before the UDF routine (spec §4.1) has run they do not exist and are read as NULL.
/// </summary>
public sealed class SapPurchaseSqlReader : ISapPurchaseReader
{
    private readonly SapSqlConnectionFactory _connections;
    private readonly ILogger<SapPurchaseSqlReader> _logger;

    private static bool? _opdnHasShipRef;
    private static bool? _opdnHasOdooShipmentId;

    public SapPurchaseSqlReader(SapSqlConnectionFactory connections, ILogger<SapPurchaseSqlReader> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Purchase Order
    // ------------------------------------------------------------------

    public async Task<SapPoHeader?> ReadPurchaseOrderAsync(int docEntry, CancellationToken ct)
    {
        const string sql = """
            SELECT T0.DocEntry, T0.DocNum, T0.CardCode, T0.CardName, T0.NumAtCard, T0.DocCur, T0.DocRate,
                   T0.DocTotal, T0.DocTotalFC, T0.DocDate, T0.DocDueDate, T0.DocStatus, T0.CANCELED
            FROM dbo.OPOR T0
            WHERE T0.DocEntry = @DocEntry
            """;

        await using var conn = await _connections.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@DocEntry", SqlDbType.Int).Value = docEntry;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        return new SapPoHeader(
            DocEntry: r.GetInt32(0),
            DocNum: r.GetInt32(1),
            CardCode: Str(r, 2) ?? string.Empty,
            CardName: Str(r, 3),
            NumAtCard: Str(r, 4),
            DocCur: Str(r, 5) ?? string.Empty,
            DocRate: Dec(r, 6),
            DocTotal: Dec(r, 7),
            DocTotalFc: Dec(r, 8),
            DocDate: r.GetDateTime(9),
            DocDueDate: NullableDate(r, 10),
            DocStatus: Str(r, 11) ?? "O",
            Canceled: Str(r, 12) ?? "N");
    }

    // ------------------------------------------------------------------
    // Goods Receipt PO
    // ------------------------------------------------------------------

    public async Task<SapGrpoHeader?> ReadGrpoAsync(int docEntry, CancellationToken ct)
    {
        await using var conn = await _connections.OpenAsync(ct);
        await EnsureOpdnColumnProbeAsync(conn, ct);

        string shipRefCol = _opdnHasShipRef == true ? "T0.U_ShipRef" : "CAST(NULL AS NVARCHAR(50))";
        string shipIdCol = _opdnHasOdooShipmentId == true ? "T0.U_OdooShipmentId" : "CAST(NULL AS NVARCHAR(30))";

        string headerSql = $"""
            SELECT T0.DocEntry, T0.DocNum, T0.CardCode, T0.DocDate, T0.DocTotal,
                   {shipRefCol} AS ShipRef, {shipIdCol} AS OdooShipmentId, T0.CANCELED
            FROM dbo.OPDN T0
            WHERE T0.DocEntry = @DocEntry
            """;

        int hDocEntry, hDocNum;
        string cardCode, canceled;
        string? shipRef, odooShipmentId;
        DateTime docDate;
        decimal docTotal;

        await using (var cmd = new SqlCommand(headerSql, conn))
        {
            cmd.Parameters.Add("@DocEntry", SqlDbType.Int).Value = docEntry;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return null;

            hDocEntry = r.GetInt32(0);
            hDocNum = r.GetInt32(1);
            cardCode = Str(r, 2) ?? string.Empty;
            docDate = r.GetDateTime(3);
            docTotal = Dec(r, 4);
            shipRef = Str(r, 5);
            odooShipmentId = Str(r, 6);
            canceled = Str(r, 7) ?? "N";
        }

        const string linesSql = """
            SELECT T1.LineNum, T1.ItemCode, T1.Dscription, T1.Quantity, T1.BaseType, T1.BaseEntry, T1.BaseLine
            FROM dbo.PDN1 T1
            WHERE T1.DocEntry = @DocEntry
            ORDER BY T1.LineNum
            """;

        var lines = new List<SapGrpoLine>();
        await using (var cmd = new SqlCommand(linesSql, conn))
        {
            cmd.Parameters.Add("@DocEntry", SqlDbType.Int).Value = docEntry;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                lines.Add(new SapGrpoLine(
                    LineNum: r.GetInt32(0),
                    ItemCode: Str(r, 1) ?? string.Empty,
                    Dscription: Str(r, 2),
                    Quantity: Dec(r, 3),
                    BaseType: NullableInt(r, 4),
                    BaseEntry: NullableInt(r, 5),
                    BaseLine: NullableInt(r, 6)));
            }
        }

        return new SapGrpoHeader(hDocEntry, hDocNum, cardCode, docDate, docTotal, shipRef, odooShipmentId, canceled, lines);
    }

    private async Task EnsureOpdnColumnProbeAsync(SqlConnection conn, CancellationToken ct)
    {
        if (_opdnHasShipRef.HasValue && _opdnHasOdooShipmentId.HasValue)
            return;

        const string sql = """
            SELECT
                CASE WHEN COL_LENGTH('dbo.OPDN', 'U_ShipRef') IS NULL THEN 0 ELSE 1 END,
                CASE WHEN COL_LENGTH('dbo.OPDN', 'U_OdooShipmentId') IS NULL THEN 0 ELSE 1 END
            """;
        await using var cmd = new SqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            _opdnHasShipRef = r.GetInt32(0) == 1;
            _opdnHasOdooShipmentId = r.GetInt32(1) == 1;
            if (_opdnHasShipRef != true || _opdnHasOdooShipmentId != true)
                _logger.LogWarning(
                    "OPDN is missing U_ShipRef and/or U_OdooShipmentId — run POST /api/admin/setup-sap-udfs. " +
                    "GRPO_LINK cannot match containers until then.");
        }
    }

    // ------------------------------------------------------------------
    // A/P Invoice / A/P Credit Memo
    // ------------------------------------------------------------------

    public async Task<SapApDocument?> ReadApDocumentAsync(int objType, int docEntry, CancellationToken ct)
    {
        var (header, lines, expenses) = objType switch
        {
            18 => ("OPCH", "PCH1", "PCH3"),
            19 => ("ORPC", "RPC1", "RPC3"),
            _ => throw new ArgumentOutOfRangeException(nameof(objType), objType, "Expected 18 (OPCH) or 19 (ORPC).")
        };

        await using var conn = await _connections.OpenAsync(ct);

        string headerSql = $"""
            SELECT T0.DocEntry, T0.DocNum, T0.CardCode, T0.CardName, T0.NumAtCard, T0.DocCur, T0.DocRate,
                   T0.DocTotal, T0.DocTotalFC, T0.DocDate, T0.DocDueDate, T0.TaxDate, T0.CANCELED, T0.DocType,
                   T0.Comments
            FROM dbo.{header} T0
            WHERE T0.DocEntry = @DocEntry
            """;

        int hDocEntry, hDocNum;
        string cardCode, docCur, canceled, docType;
        string? cardName, numAtCard, comments;
        decimal docRate, docTotal, docTotalFc;
        DateTime docDate;
        DateTime? docDueDate, taxDate;

        await using (var cmd = new SqlCommand(headerSql, conn))
        {
            cmd.Parameters.Add("@DocEntry", SqlDbType.Int).Value = docEntry;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return null;

            hDocEntry = r.GetInt32(0);
            hDocNum = r.GetInt32(1);
            cardCode = Str(r, 2) ?? string.Empty;
            cardName = Str(r, 3);
            numAtCard = Str(r, 4);
            docCur = Str(r, 5) ?? string.Empty;
            docRate = Dec(r, 6);
            docTotal = Dec(r, 7);
            docTotalFc = Dec(r, 8);
            docDate = r.GetDateTime(9);
            docDueDate = NullableDate(r, 10);
            taxDate = NullableDate(r, 11);
            canceled = Str(r, 12) ?? "N";
            docType = Str(r, 13) ?? "I";
            comments = Str(r, 14);
        }

        string linesSql = $"""
            SELECT T1.LineNum, T1.ItemCode, T1.Dscription, T1.Quantity, T1.Price, T1.LineTotal, T1.TotalFrgn,
                   T1.VatGroup, T1.AcctCode, T2.ItmsGrpCod, T1.BaseType, T1.BaseEntry, T1.BaseLine
            FROM dbo.{lines} T1
            LEFT JOIN dbo.OITM T2 ON T2.ItemCode = T1.ItemCode
            WHERE T1.DocEntry = @DocEntry
            ORDER BY T1.LineNum
            """;

        var lineList = new List<SapApLine>();
        await using (var cmd = new SqlCommand(linesSql, conn))
        {
            cmd.Parameters.Add("@DocEntry", SqlDbType.Int).Value = docEntry;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                lineList.Add(new SapApLine(
                    LineNum: r.GetInt32(0),
                    ItemCode: Str(r, 1),
                    Dscription: Str(r, 2),
                    Quantity: Dec(r, 3),
                    Price: Dec(r, 4),
                    LineTotal: Dec(r, 5),
                    TotalFrgn: Dec(r, 6),
                    VatGroup: Str(r, 7),
                    AcctCode: Str(r, 8),
                    ItmsGrpCod: NullableInt(r, 9),
                    BaseType: NullableInt(r, 10),
                    BaseEntry: NullableInt(r, 11),
                    BaseLine: NullableInt(r, 12)));
            }
        }

        string expSql = $"""
            SELECT T3.ExpnsCode, T4.ExpnsName, T3.LineTotal, T3.TotalFrgn, T3.VatGroup, T4.ExpnsAcct
            FROM dbo.{expenses} T3
            LEFT JOIN dbo.OEXD T4 ON T4.ExpnsCode = T3.ExpnsCode
            WHERE T3.DocEntry = @DocEntry
            ORDER BY T3.LineNum
            """;

        var expList = new List<SapApExpense>();
        await using (var cmd = new SqlCommand(expSql, conn))
        {
            cmd.Parameters.Add("@DocEntry", SqlDbType.Int).Value = docEntry;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                expList.Add(new SapApExpense(
                    ExpnsCode: r.GetInt32(0),
                    ExpnsName: Str(r, 1),
                    LineTotal: Dec(r, 2),
                    TotalFrgn: Dec(r, 3),
                    VatGroup: Str(r, 4),
                    ExpenseAccount: Str(r, 5)));
            }
        }

        // Purchase orders behind the document: lines drawn from a PO directly (BaseType 22) or
        // through a GRPO (BaseType 20 → PDN1 → BaseType 22).
        string poSql = $"""
            SELECT DISTINCT L.BaseEntry
            FROM dbo.{lines} L
            WHERE L.DocEntry = @DocEntry AND L.BaseType = 22 AND L.BaseEntry IS NOT NULL
            UNION
            SELECT DISTINCT P.BaseEntry
            FROM dbo.{lines} L
            INNER JOIN dbo.PDN1 P ON P.DocEntry = L.BaseEntry AND P.LineNum = L.BaseLine
            WHERE L.DocEntry = @DocEntry AND L.BaseType = 20 AND P.BaseType = 22 AND P.BaseEntry IS NOT NULL
            """;

        var poEntries = new List<int>();
        await using (var cmd = new SqlCommand(poSql, conn))
        {
            cmd.Parameters.Add("@DocEntry", SqlDbType.Int).Value = docEntry;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                poEntries.Add(r.GetInt32(0));
        }

        return new SapApDocument(
            ObjType: objType,
            DocEntry: hDocEntry,
            DocNum: hDocNum,
            CardCode: cardCode,
            CardName: cardName,
            NumAtCard: numAtCard,
            DocCur: docCur,
            DocRate: docRate,
            DocTotal: docTotal,
            DocTotalFc: docTotalFc,
            DocDate: docDate,
            DocDueDate: docDueDate,
            TaxDate: taxDate,
            Canceled: canceled,
            DocType: docType,
            Comments: comments,
            Lines: lineList,
            Expenses: expList,
            PoDocEntries: poEntries);
    }

    // ------------------------------------------------------------------
    // Backfill
    // ------------------------------------------------------------------

    public async Task<List<SapBackfillCandidate>> ListBackfillCandidatesAsync(
        DateTime fromDate, IReadOnlyCollection<string> objectTypes, CancellationToken ct)
    {
        var parts = new List<string>();
        if (objectTypes.Contains("22"))
            parts.Add("""
                SELECT '22' AS ObjectType, 'PO_UPSERT' AS EventType, DocEntry, DocNum, CardCode, NumAtCard, DocDate, DocTotal, DocCur, CANCELED
                FROM dbo.OPOR WHERE DocDate >= @From AND DocStatus = 'O' AND ISNULL(CANCELED, 'N') = 'N'
                """);
        if (objectTypes.Contains("20"))
            parts.Add("""
                SELECT '20', 'GRPO_LINK', DocEntry, DocNum, CardCode, NumAtCard, DocDate, DocTotal, DocCur, CANCELED
                FROM dbo.OPDN WHERE DocDate >= @From AND ISNULL(CANCELED, 'N') = 'N'
                """);
        if (objectTypes.Contains("18"))
            parts.Add("""
                SELECT '18', 'AP_INVOICE_CREATE', DocEntry, DocNum, CardCode, NumAtCard, DocDate, DocTotal, DocCur, CANCELED
                FROM dbo.OPCH WHERE DocDate >= @From AND ISNULL(CANCELED, 'N') = 'N'
                """);
        if (objectTypes.Contains("19"))
            parts.Add("""
                SELECT '19', 'AP_CREDIT_CREATE', DocEntry, DocNum, CardCode, NumAtCard, DocDate, DocTotal, DocCur, CANCELED
                FROM dbo.ORPC WHERE DocDate >= @From AND ISNULL(CANCELED, 'N') = 'N'
                """);

        var result = new List<SapBackfillCandidate>();
        if (parts.Count == 0)
            return result;

        string sql = string.Join("\nUNION ALL\n", parts) + "\nORDER BY DocDate, ObjectType, DocEntry";

        await using var conn = await _connections.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@From", SqlDbType.DateTime).Value = fromDate.Date;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new SapBackfillCandidate(
                ObjectType: r.GetString(0),
                EventType: r.GetString(1),
                DocEntry: r.GetInt32(2),
                DocNum: r.GetInt32(3),
                CardCode: Str(r, 4) ?? string.Empty,
                NumAtCard: Str(r, 5),
                DocDate: r.GetDateTime(6),
                DocTotal: Dec(r, 7),
                DocCur: Str(r, 8) ?? string.Empty,
                Canceled: Str(r, 9) ?? "N"));
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Readers
    // ------------------------------------------------------------------

    private static string? Str(IDataRecord r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i))?.Trim();
    private static decimal Dec(IDataRecord r, int i) => r.IsDBNull(i) ? 0m : Convert.ToDecimal(r.GetValue(i));
    private static int? NullableInt(IDataRecord r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));
    private static DateTime? NullableDate(IDataRecord r, int i) => r.IsDBNull(i) ? null : Convert.ToDateTime(r.GetValue(i));
}
