using System.Text.RegularExpressions;

namespace SapOdooMiddleware.Tests.PurchaseSync;

/// <summary>
/// Guards the stored-procedure migration against the rules of spec v4 §4.2: the six EventType
/// values are written explicitly, the procedure never sets @error outside the pre-existing
/// commented block, and the delivery block is untouched.
/// </summary>
public class TransactionNotificationMigrationTests
{
    private static string LoadMigration()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "migrations", "sqlserver")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "migrations", "sqlserver", "2026-10-20__purchases_transaction_notification.sql");
        Assert.True(File.Exists(path), $"Migration not found at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Migration_WritesEverySixEventTypeExplicitly()
    {
        var sql = LoadMigration();
        foreach (var e in new[] { "PO_UPSERT", "GRPO_LINK", "AP_INVOICE_CREATE", "AP_INVOICE_CANCEL", "AP_CREDIT_CREATE", "AP_CREDIT_CANCEL" })
            Assert.Contains($"N'{e}'", sql);
    }

    [Fact]
    public void Migration_NeverSetsErrorOutsideTheCommentedLegacyBlock()
    {
        var sql = LoadMigration();
        var matches = Regex.Matches(sql, @"SET\s+@error\s*=", RegexOptions.IgnoreCase);
        // exactly one occurrence, the legacy 42090 one inside /* ... */
        Assert.Single(matches);
        int commentStart = sql.IndexOf("/*", StringComparison.Ordinal);
        int commentEnd = sql.IndexOf("*/", StringComparison.Ordinal);
        Assert.InRange(matches[0].Index, commentStart, commentEnd);
    }

    [Fact]
    public void Migration_KeepsTheDeliveryBlockAndItsDedupe()
    {
        var sql = LoadMigration();
        Assert.Contains("IF @object_type = '15' AND @transaction_type = 'A'", sql);
        Assert.Contains("AND [ObjectType] = '15';", sql);
        Assert.Contains("[OdooSoId], [OdooDeliveryId], [BaseSODocEntry]", sql);
    }

    [Fact]
    public void Migration_NewBlocksDedupeOnPendingOnlyAndHaveTryCatch()
    {
        var sql = LoadMigration();
        foreach (var objType in new[] { "22", "20", "18", "19" })
        {
            Assert.Contains($"WHERE [ObjectType] = '{objType}' AND [DocEntry] = @p_docentry", sql);
        }
        Assert.Equal(4, Regex.Matches(sql, @"AND \[TransType\] = @transaction_type AND \[Status\] = 'pending'").Count);
        // one TRY per block (delivery + 4 new) plus the nested CATCH-insert TRYs
        Assert.True(Regex.Matches(sql, @"BEGIN TRY").Count >= 10);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_ODOO_WEBHOOK_QUEUE_Status_EventType_ObjectType_DocEntry]", sql);
    }

    [Fact]
    public void Migration_SkipsCancellationDocumentsOnAdd()
    {
        var sql = LoadMigration();
        Assert.Equal(2, Regex.Matches(sql, @"IF NOT \(@transaction_type = 'A' AND ISNULL\(@p_canceled, 'N'\) = 'C'\)").Count);
        Assert.Contains("IF ISNULL(@p_canceled, 'N') <> 'C'", sql);
    }
}
