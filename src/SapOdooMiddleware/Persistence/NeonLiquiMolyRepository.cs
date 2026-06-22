using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using SapOdooMiddleware.Configuration;
using MolasLubes.Infrastructure.Integrations.LiquiMoly;

namespace SapOdooMiddleware.Persistence;

public interface INeonLiquiMolyRepository
{
    Task<LiquiMolyProductDto?> GetByArticleNumberAsync(string articleNumber, CancellationToken ct);
    Task UpsertAsync(LiquiMolyProductDto dto, CancellationToken ct);
}

/// <summary>
/// Raw-Npgsql repository over the Neon "NeonLiquiMolyProducts" table, keyed on the
/// article number. List-valued DTO fields are stored as JSON in text columns.
///
/// NOTE: the column set below mirrors <see cref="LiquiMolyProductDto"/> and the spec (§7.1).
/// Verify it against the live Neon schema before first deploy; if column names/types
/// differ, adjust the SQL and the reader mapping in this single file.
/// </summary>
public class NeonLiquiMolyRepository : INeonLiquiMolyRepository
{
    private readonly string _connectionString;

    private static readonly JsonSerializerOptions JsonOpts = new();

    public NeonLiquiMolyRepository(IOptions<NeonSettings> settings)
        => _connectionString = settings.Value.ConnectionString;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task UpsertAsync(LiquiMolyProductDto dto, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO public."NeonLiquiMolyProducts" (
                "ArticleNumber","Name","ProductUrl",
                "Category","SubCategory","Description",
                "PackagingSize","AllPackagingSizes","Liter","SpecGrade",
                "ImageUrl","AllImageUrls",
                "PrimaryBarcode","PrimaryBarcodeUomCode","PrimaryBarcodeUomName",
                "PrimaryBarcodeUomEntry","PrimaryBarcodeBaseQtyInGroup","HasUnitBarcode",
                "Approvals","SpecificationItems","OverviewProperties",
                "Application","LiquiMolyRecommendations",
                "ProductInfoPdfUrl","SafetyDataSheetPdfUrl","IsActive","ScrapedAt"
            ) VALUES (
                @ArticleNumber,@Name,@ProductUrl,
                @Category,@SubCategory,@Description,
                @PackagingSize,@AllPackagingSizes,@Liter,@SpecGrade,
                @ImageUrl,@AllImageUrls,
                @PrimaryBarcode,@PrimaryBarcodeUomCode,@PrimaryBarcodeUomName,
                @PrimaryBarcodeUomEntry,@PrimaryBarcodeBaseQtyInGroup,@HasUnitBarcode,
                @Approvals,@SpecificationItems,@OverviewProperties,
                @Application,@LiquiMolyRecommendations,
                @ProductInfoPdfUrl,@SafetyDataSheetPdfUrl,TRUE,NOW()
            )
            ON CONFLICT ("ArticleNumber") DO UPDATE SET
                "Name"                        = EXCLUDED."Name",
                "ProductUrl"                  = EXCLUDED."ProductUrl",
                "Category"                    = EXCLUDED."Category",
                "SubCategory"                 = EXCLUDED."SubCategory",
                "Description"                 = EXCLUDED."Description",
                "PackagingSize"               = EXCLUDED."PackagingSize",
                "AllPackagingSizes"           = EXCLUDED."AllPackagingSizes",
                "Liter"                       = EXCLUDED."Liter",
                "SpecGrade"                   = EXCLUDED."SpecGrade",
                "ImageUrl"                    = EXCLUDED."ImageUrl",
                "AllImageUrls"                = EXCLUDED."AllImageUrls",
                "PrimaryBarcode"              = EXCLUDED."PrimaryBarcode",
                "PrimaryBarcodeUomCode"       = EXCLUDED."PrimaryBarcodeUomCode",
                "PrimaryBarcodeUomName"       = EXCLUDED."PrimaryBarcodeUomName",
                "PrimaryBarcodeUomEntry"      = EXCLUDED."PrimaryBarcodeUomEntry",
                "PrimaryBarcodeBaseQtyInGroup"= EXCLUDED."PrimaryBarcodeBaseQtyInGroup",
                "HasUnitBarcode"              = EXCLUDED."HasUnitBarcode",
                "Approvals"                   = EXCLUDED."Approvals",
                "SpecificationItems"          = EXCLUDED."SpecificationItems",
                "OverviewProperties"          = EXCLUDED."OverviewProperties",
                "Application"                 = EXCLUDED."Application",
                "LiquiMolyRecommendations"    = EXCLUDED."LiquiMolyRecommendations",
                "ProductInfoPdfUrl"           = EXCLUDED."ProductInfoPdfUrl",
                "SafetyDataSheetPdfUrl"       = EXCLUDED."SafetyDataSheetPdfUrl",
                "ScrapedAt"                   = NOW();
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("ArticleNumber", dto.ArticleNumber);
        cmd.Parameters.AddWithValue("Name", dto.Name);
        cmd.Parameters.AddWithValue("ProductUrl", (object?)dto.ProductUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("Category", (object?)dto.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("SubCategory", (object?)dto.SubCategory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("Description", (object?)dto.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("PackagingSize", (object?)dto.PackagingSize ?? DBNull.Value);
        AddJsonb(cmd, "AllPackagingSizes", dto.AllPackagingSizes);
        cmd.Parameters.AddWithValue("Liter", (object?)dto.Liter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("SpecGrade", (object?)dto.SpecGrade ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ImageUrl", (object?)dto.ImageUrl ?? DBNull.Value);
        AddJsonb(cmd, "AllImageUrls", dto.AllImageUrls);
        cmd.Parameters.AddWithValue("PrimaryBarcode", (object?)dto.PrimaryBarcode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("PrimaryBarcodeUomCode", (object?)dto.PrimaryBarcodeUomCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("PrimaryBarcodeUomName", (object?)dto.PrimaryBarcodeUomName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("PrimaryBarcodeUomEntry", (object?)dto.PrimaryBarcodeUomEntry ?? DBNull.Value);
        cmd.Parameters.AddWithValue("PrimaryBarcodeBaseQtyInGroup", (object?)dto.PrimaryBarcodeBaseQtyInGroup ?? DBNull.Value);
        cmd.Parameters.AddWithValue("HasUnitBarcode", dto.HasUnitBarcode);
        AddJsonb(cmd, "Approvals", dto.Approvals);
        AddJsonb(cmd, "SpecificationItems", dto.SpecificationItems);
        AddJsonb(cmd, "OverviewProperties", dto.OverviewProperties);
        cmd.Parameters.AddWithValue("Application", (object?)dto.Application ?? DBNull.Value);
        AddJsonb(cmd, "LiquiMolyRecommendations", dto.LiquiMolyRecommendations);
        cmd.Parameters.AddWithValue("ProductInfoPdfUrl", (object?)dto.ProductInfoPdfUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("SafetyDataSheetPdfUrl", (object?)dto.SafetyDataSheetPdfUrl ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<LiquiMolyProductDto?> GetByArticleNumberAsync(string articleNumber, CancellationToken ct)
    {
        const string sql = """
            SELECT "ArticleNumber","Name","ProductUrl",
                   "Category","SubCategory","Description",
                   "PackagingSize","AllPackagingSizes","Liter","SpecGrade",
                   "ImageUrl","AllImageUrls",
                   "PrimaryBarcode","PrimaryBarcodeUomCode","PrimaryBarcodeUomName",
                   "PrimaryBarcodeUomEntry","PrimaryBarcodeBaseQtyInGroup","HasUnitBarcode",
                   "Approvals","SpecificationItems","OverviewProperties",
                   "Application","LiquiMolyRecommendations",
                   "ProductInfoPdfUrl","SafetyDataSheetPdfUrl","ScrapedAt"
            FROM public."NeonLiquiMolyProducts"
            WHERE "ArticleNumber" = @ArticleNumber
            LIMIT 1;
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("ArticleNumber", articleNumber);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        return new LiquiMolyProductDto
        {
            ArticleNumber                = r.GetString(0),
            Name                         = r.GetString(1),
            ProductUrl                   = Str(r, 2),
            Category                     = Str(r, 3),
            SubCategory                  = Str(r, 4),
            Description                  = Str(r, 5),
            PackagingSize                = Str(r, 6),
            AllPackagingSizes            = JsonList(r, 7),
            Liter                        = r.IsDBNull(8) ? null : r.GetDecimal(8),
            SpecGrade                    = Str(r, 9),
            ImageUrl                     = Str(r, 10),
            AllImageUrls                 = JsonList(r, 11),
            PrimaryBarcode               = Str(r, 12),
            PrimaryBarcodeUomCode        = Str(r, 13),
            PrimaryBarcodeUomName        = Str(r, 14),
            PrimaryBarcodeUomEntry       = r.IsDBNull(15) ? null : r.GetInt32(15),
            PrimaryBarcodeBaseQtyInGroup = r.IsDBNull(16) ? null : r.GetDecimal(16),
            HasUnitBarcode               = !r.IsDBNull(17) && r.GetBoolean(17),
            Approvals                    = JsonList(r, 18),
            SpecificationItems           = JsonList(r, 19),
            OverviewProperties           = JsonList(r, 20),
            Application                  = Str(r, 21),
            LiquiMolyRecommendations     = JsonList(r, 22),
            ProductInfoPdfUrl            = Str(r, 23),
            SafetyDataSheetPdfUrl        = Str(r, 24),
            ScrapedAt                    = r.IsDBNull(25) ? null : r.GetDateTime(25),
        };
    }

    // List columns on NeonLiquiMolyProducts are text (not jsonb); store the JSON as a
    // text parameter. Read-back deserialises the same JSON string back into a List<string>.
    private static void AddJsonb(NpgsqlCommand cmd, string name, object? value)
    {
        var json = value is null ? "[]" : JsonSerializer.Serialize(value, JsonOpts);
        cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = json });
    }

    private static string? Str(NpgsqlDataReader r, int ordinal)
        => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    private static List<string> JsonList(NpgsqlDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return new List<string>();
        var raw = r.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(raw) ?? new List<string>(); }
        catch { return new List<string>(); }
    }
}
