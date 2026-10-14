using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// The invoice-lines SQL behind GET /invoices (unscoped) and return-request validation (scoped to
/// the request's invoices). Pins the unscoped text so GET /invoices cannot drift, and checks the
/// scoped IN list, de-duplication and the parameter cap.
/// </summary>
public class AutohubInvoiceLinesSqlTests
{
    // The exact query GET /invoices ran before scoping was introduced.
    private const string UnscopedSql = """
        SELECT T0.DocEntry, T0.DocNum, T0.DocDate, T0.CardCode, T0.CardName,
               T1.LineNum, T1.ItemCode,
               I.ItemName, I.U_Article_No, I.U_ItemManufacturer,
               T1.Quantity, T1.Price, T1.WhsCode
        FROM OINV T0
        JOIN INV1 T1 ON T1.DocEntry = T0.DocEntry
        JOIN OITM I ON I.ItemCode = T1.ItemCode
        WHERE T0.CardCode = @card
          AND T0.CANCELED = 'N'
          AND (@openOnly = 0 OR T0.DocStatus = 'O')
        ORDER BY T0.DocDate DESC, T0.DocNum DESC, T1.LineNum;
        """;

    [Fact]
    public void Unscoped_IsUnchangedAndHasNoInList()
    {
        var (sql, parameters) = AutohubInventorySqlService.BuildInvoiceLinesForReturnSql(null);

        Assert.Equal(UnscopedSql.ReplaceLineEndings("\n"), sql.ReplaceLineEndings("\n"));
        Assert.Empty(parameters);
    }

    [Fact]
    public void Scoped_AddsParameterisedInListBeforeOrderBy()
    {
        var (sql, parameters) = AutohubInventorySqlService.BuildInvoiceLinesForReturnSql(new[] { 101, 202 });

        Assert.Contains("AND T0.DocEntry IN (@d0, @d1)", sql);
        Assert.True(sql.IndexOf("T0.DocEntry IN", StringComparison.Ordinal)
                    < sql.IndexOf("ORDER BY", StringComparison.Ordinal));
        Assert.Equal(new[] { ("@d0", 101), ("@d1", 202) }, parameters);
        Assert.DoesNotContain("101", sql);   // values are bound, never inlined
    }

    [Fact]
    public void Scope_CollapsesDuplicates()
    {
        var scope = AutohubInventorySqlService.ScopeInvoiceDocEntries(new[] { 101, 202, 101 });

        Assert.Equal(new[] { 101, 202 }, scope);
    }

    [Fact]
    public void Scope_AtCap_StaysScoped_AboveCap_FallsBackToUnscoped()
    {
        int cap = AutohubInventorySqlService.MaxScopedInvoices;

        var atCap = AutohubInventorySqlService.ScopeInvoiceDocEntries(Enumerable.Range(1, cap).ToList());
        var aboveCap = AutohubInventorySqlService.ScopeInvoiceDocEntries(Enumerable.Range(1, cap + 1).ToList());

        Assert.NotNull(atCap);
        Assert.Equal(cap, atCap!.Count);
        Assert.Null(aboveCap);
    }
}
