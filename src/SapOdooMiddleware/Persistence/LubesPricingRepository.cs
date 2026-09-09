using Microsoft.Extensions.Options;
using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>The effective EUR→TZS rate and where it came from.</summary>
public record EffectiveRate(decimal Rate, string Source, DateTime? UpdatedAt);

/// <summary>One audit entry of a price change (all prices NET TZS).</summary>
public record PriceChangeEntry(
    string ItemCode,
    decimal? OldPl1, decimal? OldPl2, decimal? OldPl3, decimal? OldPl4,
    decimal NewPl1, decimal NewPl2, decimal NewPl3, decimal NewPl4,
    decimal? EurCost, decimal? Rate, string? PricingCategory,
    string Mode, string? Note, DateTime ChangedAt);

/// <summary>Item eligible for a filter-based bulk reprice (has a stored EUR cost).</summary>
public record RepriceCandidate(string ItemCode, decimal LastEurCost, int? ItemGroupCode);

/// <summary>
/// Lubes pricing operations state in Neon: the runtime-editable EUR→TZS rate,
/// the price-change audit log, and the per-item pricing-input stamps on NeonProducts
/// (LastEurCost / LastEurTzsRate / LastPricedAt). Schema is ensured lazily and
/// idempotently (CREATE/ALTER IF NOT EXISTS) so no manual migration is required.
/// </summary>
public interface ILubesPricingRepository
{
    /// <summary>Effective rate: Neon override when set, else the Pricing:EurTzsRate config default.</summary>
    Task<EffectiveRate> GetEffectiveRateAsync(CancellationToken ct);

    /// <summary>Sets the runtime EUR→TZS rate override (used by provisioning and repricing).</summary>
    Task SetRateAsync(decimal rate, CancellationToken ct);

    Task LogChangeAsync(PriceChangeEntry entry, CancellationToken ct);
    Task<IReadOnlyList<PriceChangeEntry>> GetHistoryAsync(string itemCode, int limit, CancellationToken ct);

    /// <summary>Stamps the inputs an item was last priced with onto its NeonProducts row.</summary>
    Task StampPricingInputsAsync(string itemCode, decimal eurCost, decimal rate, CancellationToken ct);

    /// <summary>Items with a stored EUR cost, optionally filtered by SAP group code(s).</summary>
    Task<IReadOnlyList<RepriceCandidate>> GetRepriceCandidatesAsync(
        IReadOnlyCollection<int>? sapGroupCodes, CancellationToken ct);

    // ── Ratio overrides (the runtime-editable calculator tables) ─────

    Task<(List<Pricing.BandRatioOverride> Band, List<Pricing.MaasaiRatioOverride> Maasai)>
        GetRatioOverridesAsync(CancellationToken ct);

    Task UpsertBandRatioOverrideAsync(Pricing.BandRatioOverride o, string? note, CancellationToken ct);
    Task UpsertMaasaiRatioOverrideAsync(Pricing.MaasaiRatioOverride o, string? note, CancellationToken ct);

    /// <summary>Removes an override so the category+band reverts to the hard-coded default.</summary>
    Task DeleteBandRatioOverrideAsync(string category, string band, CancellationToken ct);
    Task DeleteMaasaiRatioOverrideAsync(string category, int bandIndex, CancellationToken ct);
}

public class LubesPricingRepository : ILubesPricingRepository
{
    private const string RateKey = "EurTzsRate";
    private static readonly SemaphoreSlim SchemaLock = new(1, 1);
    private static volatile bool _schemaEnsured;

    private readonly string _connectionString;
    private readonly PricingSettings _pricingSettings;

    public LubesPricingRepository(IOptions<NeonSettings> neon, IOptions<PricingSettings> pricing)
    {
        _connectionString = neon.Value.ConnectionString;
        _pricingSettings = pricing.Value;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        return conn;
    }

    private static async Task EnsureSchemaAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (_schemaEnsured) return;
        await SchemaLock.WaitAsync(ct);
        try
        {
            if (_schemaEnsured) return;
            const string sql = """
                CREATE TABLE IF NOT EXISTS public."pricing_settings" (
                    "Key"       text PRIMARY KEY,
                    "Value"     text NOT NULL,
                    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
                );
                CREATE TABLE IF NOT EXISTS public."price_change_log" (
                    "Id"              bigserial PRIMARY KEY,
                    "ItemCode"        text NOT NULL,
                    "OldPl1"          numeric NULL, "OldPl2" numeric NULL,
                    "OldPl3"          numeric NULL, "OldPl4" numeric NULL,
                    "NewPl1"          numeric NOT NULL, "NewPl2" numeric NOT NULL,
                    "NewPl3"          numeric NOT NULL, "NewPl4" numeric NOT NULL,
                    "EurCost"         numeric NULL,
                    "Rate"            numeric NULL,
                    "PricingCategory" text NULL,
                    "Mode"            text NOT NULL,
                    "Note"            text NULL,
                    "ChangedAt"       timestamptz NOT NULL DEFAULT now()
                );
                CREATE INDEX IF NOT EXISTS "ix_price_change_log_item"
                    ON public."price_change_log" ("ItemCode", "ChangedAt" DESC);
                ALTER TABLE public."NeonProducts"
                    ADD COLUMN IF NOT EXISTS "LastEurCost"    numeric NULL,
                    ADD COLUMN IF NOT EXISTS "LastEurTzsRate" numeric NULL,
                    ADD COLUMN IF NOT EXISTS "LastPricedAt"   timestamptz NULL;
                CREATE TABLE IF NOT EXISTS public."pricing_band_ratio_overrides" (
                    "Category"  text NOT NULL,
                    "Band"      text NOT NULL,
                    "SpRatio"     numeric NOT NULL,
                    "DealerRatio" numeric NOT NULL,
                    "RetailRatio" numeric NOT NULL,
                    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY ("Category", "Band")
                );
                CREATE TABLE IF NOT EXISTS public."pricing_maasai_ratio_overrides" (
                    "Category"  text NOT NULL,
                    "BandIndex" int  NOT NULL,
                    "Ratio"     numeric NOT NULL,
                    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY ("Category", "BandIndex")
                );
                CREATE TABLE IF NOT EXISTS public."ratio_change_log" (
                    "Id"        bigserial PRIMARY KEY,
                    "Kind"      text NOT NULL,       -- band | maasai | reset
                    "Category"  text NOT NULL,
                    "Band"      text NOT NULL,
                    "NewValues" text NOT NULL,
                    "Note"      text NULL,
                    "ChangedAt" timestamptz NOT NULL DEFAULT now()
                );
                """;
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
            _schemaEnsured = true;
        }
        finally
        {
            SchemaLock.Release();
        }
    }

    public async Task<EffectiveRate> GetEffectiveRateAsync(CancellationToken ct)
    {
        const string sql = """SELECT "Value", "UpdatedAt" FROM public."pricing_settings" WHERE "Key" = @k;""";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("k", RateKey);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct) && decimal.TryParse(r.GetString(0), out var rate) && rate > 0)
            return new EffectiveRate(rate, "pricing_settings (UI)", r.GetDateTime(1));

        return new EffectiveRate(_pricingSettings.EurTzsRate, "Pricing:EurTzsRate config default", null);
    }

    public async Task SetRateAsync(decimal rate, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO public."pricing_settings" ("Key","Value","UpdatedAt")
            VALUES (@k, @v, now())
            ON CONFLICT ("Key") DO UPDATE SET "Value" = EXCLUDED."Value", "UpdatedAt" = now();
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("k", RateKey);
        cmd.Parameters.AddWithValue("v", rate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task LogChangeAsync(PriceChangeEntry e, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO public."price_change_log"
                ("ItemCode","OldPl1","OldPl2","OldPl3","OldPl4",
                 "NewPl1","NewPl2","NewPl3","NewPl4",
                 "EurCost","Rate","PricingCategory","Mode","Note")
            VALUES (@item,@o1,@o2,@o3,@o4,@n1,@n2,@n3,@n4,@eur,@rate,@cat,@mode,@note);
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("item", e.ItemCode);
        cmd.Parameters.AddWithValue("o1", (object?)e.OldPl1 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("o2", (object?)e.OldPl2 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("o3", (object?)e.OldPl3 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("o4", (object?)e.OldPl4 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("n1", e.NewPl1);
        cmd.Parameters.AddWithValue("n2", e.NewPl2);
        cmd.Parameters.AddWithValue("n3", e.NewPl3);
        cmd.Parameters.AddWithValue("n4", e.NewPl4);
        cmd.Parameters.AddWithValue("eur", (object?)e.EurCost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rate", (object?)e.Rate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cat", (object?)e.PricingCategory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("mode", e.Mode);
        cmd.Parameters.AddWithValue("note", (object?)e.Note ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PriceChangeEntry>> GetHistoryAsync(
        string itemCode, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT "ItemCode","OldPl1","OldPl2","OldPl3","OldPl4",
                   "NewPl1","NewPl2","NewPl3","NewPl4",
                   "EurCost","Rate","PricingCategory","Mode","Note","ChangedAt"
            FROM public."price_change_log"
            WHERE "ItemCode" = @item
            ORDER BY "ChangedAt" DESC
            LIMIT @limit;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("item", itemCode);
        cmd.Parameters.AddWithValue("limit", limit);

        var list = new List<PriceChangeEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new PriceChangeEntry(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetDecimal(1), r.IsDBNull(2) ? null : r.GetDecimal(2),
                r.IsDBNull(3) ? null : r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetDecimal(4),
                r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8),
                r.IsDBNull(9) ? null : r.GetDecimal(9),
                r.IsDBNull(10) ? null : r.GetDecimal(10),
                r.IsDBNull(11) ? null : r.GetString(11),
                r.GetString(12),
                r.IsDBNull(13) ? null : r.GetString(13),
                r.GetDateTime(14)));
        }
        return list;
    }

    public async Task StampPricingInputsAsync(
        string itemCode, decimal eurCost, decimal rate, CancellationToken ct)
    {
        const string sql = """
            UPDATE public."NeonProducts"
            SET "LastEurCost" = @eur, "LastEurTzsRate" = @rate, "LastPricedAt" = now()
            WHERE "ItemCode" = @item;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("item", itemCode);
        cmd.Parameters.AddWithValue("eur", eurCost);
        cmd.Parameters.AddWithValue("rate", rate);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RepriceCandidate>> GetRepriceCandidatesAsync(
        IReadOnlyCollection<int>? sapGroupCodes, CancellationToken ct)
    {
        var sql = """
            SELECT "ItemCode", "LastEurCost", "ItemGroupCode"
            FROM public."NeonProducts"
            WHERE "LastEurCost" IS NOT NULL AND "LastEurCost" > 0
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;
        if (sapGroupCodes is { Count: > 0 })
        {
            var names = sapGroupCodes.Select((g, i) =>
            {
                cmd.Parameters.AddWithValue($"g{i}", g);
                return $"@g{i}";
            });
            sql += $"\n  AND \"ItemGroupCode\" IN ({string.Join(",", names)})";
        }
        sql += "\nORDER BY \"ItemCode\";";
        cmd.CommandText = sql;

        var list = new List<RepriceCandidate>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new RepriceCandidate(
                r.GetString(0), r.GetDecimal(1),
                r.IsDBNull(2) ? null : r.GetInt32(2)));
        }
        return list;
    }

    // ── Ratio overrides ──────────────────────────────────────────────

    public async Task<(List<Pricing.BandRatioOverride> Band, List<Pricing.MaasaiRatioOverride> Maasai)>
        GetRatioOverridesAsync(CancellationToken ct)
    {
        var band = new List<Pricing.BandRatioOverride>();
        var maasai = new List<Pricing.MaasaiRatioOverride>();

        await using var conn = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """SELECT "Category","Band","SpRatio","DealerRatio","RetailRatio" FROM public."pricing_band_ratio_overrides";""",
            conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                band.Add(new Pricing.BandRatioOverride(
                    r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4)));
        }
        await using (var cmd = new NpgsqlCommand(
            """SELECT "Category","BandIndex","Ratio" FROM public."pricing_maasai_ratio_overrides";""",
            conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                maasai.Add(new Pricing.MaasaiRatioOverride(r.GetString(0), r.GetInt32(1), r.GetDecimal(2)));
        }
        return (band, maasai);
    }

    public async Task UpsertBandRatioOverrideAsync(
        Pricing.BandRatioOverride o, string? note, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO public."pricing_band_ratio_overrides"
                ("Category","Band","SpRatio","DealerRatio","RetailRatio","UpdatedAt")
            VALUES (@c,@b,@sp,@d,@r,now())
            ON CONFLICT ("Category","Band") DO UPDATE SET
                "SpRatio" = EXCLUDED."SpRatio", "DealerRatio" = EXCLUDED."DealerRatio",
                "RetailRatio" = EXCLUDED."RetailRatio", "UpdatedAt" = now();
            INSERT INTO public."ratio_change_log" ("Kind","Category","Band","NewValues","Note")
            VALUES ('band', @c, @b, @vals, @note);
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("c", o.Category);
        cmd.Parameters.AddWithValue("b", o.Band);
        cmd.Parameters.AddWithValue("sp", o.Sp);
        cmd.Parameters.AddWithValue("d", o.Dealer);
        cmd.Parameters.AddWithValue("r", o.Retail);
        cmd.Parameters.AddWithValue("vals", $"sp={o.Sp} dealer={o.Dealer} retail={o.Retail}");
        cmd.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertMaasaiRatioOverrideAsync(
        Pricing.MaasaiRatioOverride o, string? note, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO public."pricing_maasai_ratio_overrides" ("Category","BandIndex","Ratio","UpdatedAt")
            VALUES (@c,@i,@r,now())
            ON CONFLICT ("Category","BandIndex") DO UPDATE SET
                "Ratio" = EXCLUDED."Ratio", "UpdatedAt" = now();
            INSERT INTO public."ratio_change_log" ("Kind","Category","Band","NewValues","Note")
            VALUES ('maasai', @c, @band, @vals, @note);
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("c", o.Category);
        cmd.Parameters.AddWithValue("i", o.BandIndex);
        cmd.Parameters.AddWithValue("r", o.Ratio);
        cmd.Parameters.AddWithValue("band", $"maasai-band-{o.BandIndex}");
        cmd.Parameters.AddWithValue("vals", $"ratio={o.Ratio}");
        cmd.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteBandRatioOverrideAsync(string category, string band, CancellationToken ct)
    {
        const string sql = """
            DELETE FROM public."pricing_band_ratio_overrides" WHERE "Category" = @c AND "Band" = @b;
            INSERT INTO public."ratio_change_log" ("Kind","Category","Band","NewValues","Note")
            VALUES ('reset', @c, @b, 'reverted to default', NULL);
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("c", category);
        cmd.Parameters.AddWithValue("b", band);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteMaasaiRatioOverrideAsync(string category, int bandIndex, CancellationToken ct)
    {
        const string sql = """
            DELETE FROM public."pricing_maasai_ratio_overrides" WHERE "Category" = @c AND "BandIndex" = @i;
            INSERT INTO public."ratio_change_log" ("Kind","Category","Band","NewValues","Note")
            VALUES ('reset', @c, @band, 'reverted to default', NULL);
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("c", category);
        cmd.Parameters.AddWithValue("i", bandIndex);
        cmd.Parameters.AddWithValue("band", $"maasai-band-{bandIndex}");
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
