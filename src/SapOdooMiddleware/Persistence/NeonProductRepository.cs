using Microsoft.Extensions.Options;
using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

public record NeonProductWrite(
    string ItemCode,
    string ItemName,
    int    ItemsGroupCode,
    string OdooCategoryExternalId,
    string OdooCategoryName,
    decimal ListPrice,
    string SapStatus = "created",
    string? SapErrorMsg = null);

public record NeonProductForBackref(string ItemCode, string OdooProductId);

public interface INeonProductRepository
{
    Task UpsertProductAsync(NeonProductWrite write, CancellationToken ct);

    Task UpsertPricesAsync(
        string itemCode, decimal retailNet, decimal dealerNet, decimal superDealerNet,
        CancellationToken ct);

    /// <summary>Items whose Odoo product was created but the SAP UDF is not yet stamped.</summary>
    Task<IReadOnlyList<NeonProductForBackref>> GetItemsAwaitingBackrefAsync(
        int limit, CancellationToken ct);

    Task MarkBackrefStampedAsync(string itemCode, CancellationToken ct);
}

/// <summary>
/// Raw-Npgsql repository over the Neon "NeonProducts" / "NeonPriceLists" tables.
/// Column set follows the schema described in the implementation spec (§7);
/// adjust the SQL here if the live Neon schema differs.
/// </summary>
public class NeonProductRepository : INeonProductRepository
{
    private readonly string _connectionString;

    public NeonProductRepository(IOptions<NeonSettings> settings)
        => _connectionString = settings.Value.ConnectionString;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task UpsertProductAsync(NeonProductWrite write, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO public."NeonProducts" (
                "ItemCode","ItemName","ItemGroupCode",
                "OdooCategoryExternalId","OdooCategoryName",
                "ProductType","IsStorable","OdooUomId",
                "SalesTaxId","PurchaseTaxId","IncomeAccountId","ExpenseAccountId",
                "CompanyId","ListPrice","SapItemType",
                "SapVatGroupSales","SapVatGroupPurchase","SapUomGroupEntry",
                "SapStatus","SapErrorMsg",
                "IsInventoryItem","IsActive",
                "OnHandSap"
            ) VALUES (
                @ItemCode,@ItemName,@ItemsGroupCode,
                @OdooCategoryExternalId,@OdooCategoryName,
                'consu',true,1,
                5,2,26,31,
                1,@ListPrice,'I',
                'O1','I1',1,
                @SapStatus,@SapErrorMsg,
                true,true,
                0
            )
            ON CONFLICT ("ItemCode") DO UPDATE SET
                "ItemName"               = EXCLUDED."ItemName",
                "ItemGroupCode"          = EXCLUDED."ItemGroupCode",
                "OdooCategoryExternalId" = EXCLUDED."OdooCategoryExternalId",
                "OdooCategoryName"       = EXCLUDED."OdooCategoryName",
                "ListPrice"              = EXCLUDED."ListPrice",
                "SapStatus"              = EXCLUDED."SapStatus",
                "SapErrorMsg"            = EXCLUDED."SapErrorMsg";
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("ItemCode", write.ItemCode);
        cmd.Parameters.AddWithValue("ItemName", write.ItemName);
        cmd.Parameters.AddWithValue("ItemsGroupCode", write.ItemsGroupCode);
        cmd.Parameters.AddWithValue("OdooCategoryExternalId", (object?)write.OdooCategoryExternalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("OdooCategoryName", (object?)write.OdooCategoryName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ListPrice", write.ListPrice);
        cmd.Parameters.AddWithValue("SapStatus", write.SapStatus);
        cmd.Parameters.AddWithValue("SapErrorMsg", (object?)write.SapErrorMsg ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertPricesAsync(
        string itemCode, decimal retailNet, decimal dealerNet, decimal superDealerNet,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO public."NeonPriceLists" ("ItemCode","PriceList","Price") VALUES
                (@ItemCode, 1, @Retail),
                (@ItemCode, 2, @Dealer),
                (@ItemCode, 3, @SuperDealer)
            ON CONFLICT ("ItemCode","PriceList") DO UPDATE SET "Price" = EXCLUDED."Price";
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("ItemCode", itemCode);
        cmd.Parameters.AddWithValue("Retail", retailNet);
        cmd.Parameters.AddWithValue("Dealer", dealerNet);
        cmd.Parameters.AddWithValue("SuperDealer", superDealerNet);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<NeonProductForBackref>> GetItemsAwaitingBackrefAsync(
        int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT "ItemCode","OdooProductId"
            FROM public."NeonProducts"
            WHERE "SapStatus" = 'created'
              AND "OdooProductId" IS NOT NULL
              AND "OdooProductId" <> ''
              AND "BackrefStampedAt" IS NULL
            LIMIT @Limit;
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Limit", limit);

        var results = new List<NeonProductForBackref>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new NeonProductForBackref(reader.GetString(0), reader.GetString(1)));
        return results;
    }

    public async Task MarkBackrefStampedAsync(string itemCode, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."NeonProducts"
            SET "BackrefStampedAt" = now()
            WHERE "ItemCode" = @ItemCode;
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("ItemCode", itemCode);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
