using Npgsql;
using NpgsqlTypes;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>
/// A catalogue card (oitm row) carrying a SAP item code while describing another part: its article number
/// differs from the item's article in the SAP mirror. CorrectOitmId / CorrectItemCode: the card that does
/// have the item's article, if any (preferring one without an item code).
/// </summary>
public sealed record MislinkedCard(
    long WrongOitmId, string ItemCode, string? SapBrand, string MirrorArticle, string? WrongArticle,
    string? WrongName, long? CorrectOitmId, string? CorrectItemCode);

/// <summary>One recorded repair (catalogue_card_repair).</summary>
public sealed record CardRepairRecord(
    long Id, Guid RunId, string ItemCode, string Action, long WrongOitmId, long? CorrectOitmId, long? NewOitmId,
    string SapArticle, DateTime? UndoneAt);

public interface ICatalogueCardRepairRepository
{
    /// <summary>Mislinked cards whose SAP brand's first word is one of <paramref name="brands"/> (all brands when empty).</summary>
    Task<IReadOnlyList<MislinkedCard>> ListMislinkedAsync(IReadOnlyCollection<string> brands, CancellationToken ct);

    /// <summary>
    /// Moves the item code from the wrong card to the free correct card and records it, in one transaction.
    /// False (nothing changed) when the wrong card no longer carries the code or the correct card is taken.
    /// </summary>
    Task<bool> MoveToCorrectCardAsync(Guid runId, MislinkedCard card, string sapArticle, string user, CancellationToken ct);

    /// <summary>
    /// Takes the item code off the wrong card, makes a new card (article, supplier, item code, source
    /// 'molas_card_repair', OEM cross-references) and records it, in one transaction. Null (nothing changed)
    /// when the wrong card no longer carries the code.
    /// </summary>
    Task<long?> MoveToNewCardAsync(Guid runId, MislinkedCard card, string sapArticle, IReadOnlyList<string> oems,
        string user, CancellationToken ct);

    /// <summary>Puts back every not-yet-undone change of a run. Returns how many items were put back.</summary>
    Task<int> UndoRunAsync(Guid runId, CancellationToken ct);
}

/// <summary>Autohub Neon (Parts_Catalog); table from migrations/2026-11-18__catalogue_card_repair.sql.</summary>
public sealed class CatalogueCardRepairRepository : ICatalogueCardRepairRepository
{
    public const string RepairSource = "molas_card_repair";

    private readonly ICompanyContext _company;
    public CatalogueCardRepairRepository(ICompanyContext company) => _company = company;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_company.Current.Neon.ConnectionString);
        await c.OpenAsync(ct);
        return c;
    }

    private const string Norm = "upper(regexp_replace(COALESCE({0}, ''), '[^A-Za-z0-9]', '', 'g'))";
    private static string N(string expr) => string.Format(Norm, expr);

    public async Task<IReadOnlyList<MislinkedCard>> ListMislinkedAsync(IReadOnlyCollection<string> brands, CancellationToken ct)
    {
        var sql = $"""
            WITH mis AS (
                SELECT o.id AS wrong_id, o.item_code, btrim(p."U_MdlTEST") AS brand, btrim(p."U_Article_No") AS sap_article,
                       o.article_number AS wrong_article, o.name AS wrong_name
                FROM public.oitm o
                JOIN public."NeonAutoHubProducts" p ON p."ItemCode" = o.item_code
                WHERE COALESCE(o.article_number, '') <> '' AND COALESCE(p."U_Article_No", '') <> ''
                  AND {N("o.article_number")} <> {N("p.\"U_Article_No\"")}
                  AND (cardinality(@brands) = 0 OR upper(split_part(btrim(p."U_MdlTEST"), ' ', 1)) = ANY(@brands))
            )
            SELECT m.wrong_id, m.item_code, m.brand, m.sap_article, m.wrong_article, m.wrong_name, c.id, c.item_code
            FROM mis m
            LEFT JOIN LATERAL (
                SELECT x.id, x.item_code FROM public.oitm x
                WHERE {N("x.article_number")} = {N("m.sap_article")} AND x.id <> m.wrong_id
                ORDER BY (COALESCE(x.item_code, '') = '') DESC, x.id
                LIMIT 1) c ON true
            ORDER BY m.brand, m.item_code;
            """;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("brands", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = brands.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim().ToUpperInvariant()).ToArray(),
        });
        var list = new List<MislinkedCard>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            list.Add(new MislinkedCard(r.GetInt64(0), r.GetString(1), Str(2), r.GetString(3), Str(4), Str(5),
                r.IsDBNull(6) ? null : r.GetInt64(6), Str(7)));
        }
        return list;
    }

    // Takes the code off the wrong card; false when it no longer carries it (already repaired or changed).
    private static async Task<bool> ClearWrongAsync(NpgsqlConnection conn, NpgsqlTransaction tx, MislinkedCard card, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE public.oitm SET item_code = NULL, write_date = now() WHERE id = @id AND item_code = @code;", conn, tx);
        cmd.Parameters.AddWithValue("id", card.WrongOitmId);
        cmd.Parameters.AddWithValue("code", card.ItemCode);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task RecordAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid runId, MislinkedCard card,
        string action, long? correctId, long? newId, string sapArticle, int oemCount, string user, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO public.catalogue_card_repair
                (run_id, item_code, action, wrong_oitm_id, correct_oitm_id, new_oitm_id, sap_article, wrong_article, oem_count, done_by)
            VALUES (@run, @code, @action, @wrong, @correct, @new, @article, @wrongArticle, @oems, @user);
            """, conn, tx);
        cmd.Parameters.AddWithValue("run", runId);
        cmd.Parameters.AddWithValue("code", card.ItemCode);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("wrong", card.WrongOitmId);
        cmd.Parameters.AddWithValue("correct", (object?)correctId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("new", (object?)newId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("article", sapArticle);
        cmd.Parameters.AddWithValue("wrongArticle", (object?)card.WrongArticle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("oems", oemCount);
        cmd.Parameters.AddWithValue("user", user);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> MoveToCorrectCardAsync(Guid runId, MislinkedCard card, string sapArticle, string user, CancellationToken ct)
    {
        if (card.CorrectOitmId is not { } correctId) return false;
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!await ClearWrongAsync(conn, tx, card, ct)) { await tx.RollbackAsync(ct); return false; }

        await using (var cmd = new NpgsqlCommand(
            "UPDATE public.oitm SET item_code = @code, write_date = now() WHERE id = @id AND (COALESCE(item_code, '') = '' OR item_code = @code);", conn, tx))
        {
            cmd.Parameters.AddWithValue("code", card.ItemCode);
            cmd.Parameters.AddWithValue("id", correctId);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) { await tx.RollbackAsync(ct); return false; }
        }
        await RecordAsync(conn, tx, runId, card, "linked", correctId, null, sapArticle, 0, user, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<long?> MoveToNewCardAsync(Guid runId, MislinkedCard card, string sapArticle, IReadOnlyList<string> oems,
        string user, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!await ClearWrongAsync(conn, tx, card, ct)) { await tx.RollbackAsync(ct); return null; }

        long newId;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO public.oitm (article_number, supplier_name, item_code, source)
            VALUES (@article, @supplier, @code, @source) RETURNING id;
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("article", sapArticle);
            cmd.Parameters.AddWithValue("supplier", (object?)card.SapBrand ?? DBNull.Value);
            cmd.Parameters.AddWithValue("code", card.ItemCode);
            cmd.Parameters.AddWithValue("source", RepairSource);
            newId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }

        if (oems.Count > 0)
        {
            // Never an internal item code as an OEM (same guard as the bridge).
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO public.oitm_cross_reference (oitm_id, oem_number, reference_type)
                SELECT @id, t, 'oem' FROM unnest(@oems::text[]) t
                WHERE NOT EXISTS (SELECT 1 FROM public.oitm o WHERE o.item_code = t);
                """, conn, tx);
            cmd.Parameters.AddWithValue("id", newId);
            cmd.Parameters.AddWithValue("oems", oems.ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await RecordAsync(conn, tx, runId, card, "created", null, newId, sapArticle, oems.Count, user, ct);
        await tx.CommitAsync(ct);
        return newId;
    }

    public async Task<int> UndoRunAsync(Guid runId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var records = new List<CardRepairRecord>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT id, run_id, item_code, action, wrong_oitm_id, correct_oitm_id, new_oitm_id, sap_article, undone_at
            FROM public.catalogue_card_repair WHERE run_id = @run AND undone_at IS NULL ORDER BY id;
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("run", runId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                records.Add(new CardRepairRecord(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
                    r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.GetString(7), null));
        }

        foreach (var rec in records)
        {
            async Task Exec(string sql, params (string Name, object Value)[] p)
            {
                await using var cmd = new NpgsqlCommand(sql, conn, tx);
                foreach (var (name, value) in p) cmd.Parameters.AddWithValue(name, value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (rec.Action == "linked" && rec.CorrectOitmId is { } correct)
                await Exec("UPDATE public.oitm SET item_code = NULL, write_date = now() WHERE id = @id AND item_code = @code;",
                    ("id", correct), ("code", rec.ItemCode));
            else if (rec.Action == "created" && rec.NewOitmId is { } created)
            {
                await Exec("DELETE FROM public.oitm_cross_reference WHERE oitm_id = @id;", ("id", created));
                await Exec("DELETE FROM public.oitm WHERE id = @id AND source = @source;", ("id", created), ("source", RepairSource));
            }
            await Exec("UPDATE public.oitm SET item_code = @code, write_date = now() WHERE id = @id AND COALESCE(item_code, '') = '';",
                ("code", rec.ItemCode), ("id", rec.WrongOitmId));
            await Exec("UPDATE public.catalogue_card_repair SET undone_at = now() WHERE id = @id;", ("id", rec.Id));
        }
        await tx.CommitAsync(ct);
        return records.Count;
    }
}
