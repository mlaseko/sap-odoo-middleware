using System.Text.RegularExpressions;

namespace SapOdooMiddleware.Tests.PurchaseSync;

/// <summary>
/// Guards the stored-procedure migration against the rules of spec v4 §4.2: the six EventType
/// values are written explicitly, the procedure sets @error only in the pre-existing commented
/// block and the one GRPO container-number check, and the delivery block is untouched.
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
    public void Migration_SetsErrorOnlyInTheLegacyBlockAndThePurchaseChecks()
    {
        var sql = LoadMigration();
        var matches = Regex.Matches(sql, @"SET\s+@error\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        int commentStart = sql.IndexOf("/*", StringComparison.Ordinal);
        int commentEnd = sql.IndexOf("*/", StringComparison.Ordinal);
        // the legacy 42090 one inside /* ... */ ...
        Assert.InRange(matches[0].Index, commentStart, commentEnd);
        // ... then only the purchase checks: g container number (42100), j GRPO copied from a PO (42103),
        // h zero price unless free on GRPO and A/P invoice (42101 twice), i vendor reference (42102)
        var codes = matches.Skip(1).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "42100", "42103", "42101", "42101", "42102" }, codes);
        // the queue blocks never set @error: every check comes before the first queue block
        int firstQueue = sql.IndexOf("IF @object_type = '20' AND @transaction_type IN ('A', 'U') AND @error = 0", StringComparison.Ordinal);
        Assert.All(matches.Skip(1), m => Assert.True(m.Index < firstQueue));
    }

    [Fact]
    public void Migration_RefusesALiquiMolyGrpoLineNotCopiedFromAPurchaseOrder()
    {
        var sql = LoadMigration();
        Assert.Contains("ISNULL(T1.[BaseType], -1) <> 22", sql);
        int rule = sql.IndexOf("SET @error = 42103", StringComparison.Ordinal);
        int block = sql.LastIndexOf("IF @object_type = '20' AND @transaction_type = 'A' AND @error = 0", rule, StringComparison.Ordinal);
        Assert.True(block > 0 && block < rule);
        Assert.Contains("T0.[CardCode] = N'S00001'", sql.Substring(block, rule - block));
    }

    [Fact]
    public void Migration_RefusesANewGrpoWithoutTheContainerNumber_AndQueuesOnlyWhenAccepted()
    {
        var sql = LoadMigration();
        int check = sql.IndexOf("IF @object_type = '20' AND @transaction_type = 'A'\n", StringComparison.Ordinal);
        if (check < 0)
            check = sql.IndexOf("IF @object_type = '20' AND @transaction_type = 'A'\r\n", StringComparison.Ordinal);
        int queue = sql.IndexOf("IF @object_type = '20' AND @transaction_type IN ('A', 'U') AND @error = 0", StringComparison.Ordinal);
        Assert.True(check > 0, "GRPO container-number check not found");
        Assert.True(queue > check, "the GRPO queue block must follow the check and skip a refused receipt");
        Assert.Contains("LTRIM(RTRIM(ISNULL(T0.[U_ShipRef], N''))) = N''", sql);
        Assert.Contains("ISNULL(T0.[CANCELED], 'N') <> 'C'", sql);
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
