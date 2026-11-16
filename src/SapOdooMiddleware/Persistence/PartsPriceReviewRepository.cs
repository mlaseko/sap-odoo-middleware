using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>
/// A saved price decision for one Autohub invoice line (parts_price_review). Carries the inputs the
/// decision was based on, so a later read can tell whether it is still valid.
/// </summary>
public sealed record PartsPriceDecision(
    Guid LineId, Guid DocumentId, string Status,
    decimal? UnitPriceForeign, decimal? DiscountPct, string? Currency, decimal? ForexRate, decimal? CifTzs,
    decimal? Pl01, decimal? FormulaPl03, decimal? FormulaPl05,
    decimal? FinalPl03, decimal? FinalPl05, string? Reason,
    string ReviewedBy, DateTime ReviewedAt);

public interface IPartsPriceReviewRepository
{
    /// <summary>Every saved decision for the document, keyed by line id.</summary>
    Task<IReadOnlyDictionary<Guid, PartsPriceDecision>> GetByDocumentAsync(Guid documentId, CancellationToken ct);

    /// <summary>Insert or replace the decisions (one row per line), all in one transaction.</summary>
    Task UpsertAsync(IReadOnlyList<PartsPriceDecision> decisions, CancellationToken ct);

    /// <summary>Delete a line's decision. False when the document had no decision for that line.</summary>
    Task<bool> DeleteAsync(Guid documentId, Guid lineId, CancellationToken ct);
}

/// <summary>
/// Reads parts_price_review in parts_catalog. Connection per-tenant via ICompanyContext (the
/// /api/autohub routes resolve to the Autohub tenant).
/// </summary>
public sealed class PartsPriceReviewRepository : IPartsPriceReviewRepository
{
    private readonly ICompanyContext _company;
    public PartsPriceReviewRepository(ICompanyContext company) => _company = company;

    public async Task<IReadOnlyDictionary<Guid, PartsPriceDecision>> GetByDocumentAsync(
        Guid documentId, CancellationToken ct)
    {
        const string sql = """
            SELECT line_id, document_id, status,
                   unit_price_foreign, discount_pct, currency, forex_rate, cif_tzs,
                   pl01, formula_pl03, formula_pl05,
                   final_pl03, final_pl05, reason,
                   reviewed_by, reviewed_at
            FROM public.parts_price_review
            WHERE document_id = @doc;
            """;
        await using var conn = new NpgsqlConnection(_company.Current.Neon.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);

        var map = new Dictionary<Guid, PartsPriceDecision>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            decimal? Dec(int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
            string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);

            var d = new PartsPriceDecision(
                LineId: r.GetGuid(0), DocumentId: r.GetGuid(1), Status: r.GetString(2),
                UnitPriceForeign: Dec(3), DiscountPct: Dec(4), Currency: Str(5), ForexRate: Dec(6), CifTzs: Dec(7),
                Pl01: Dec(8), FormulaPl03: Dec(9), FormulaPl05: Dec(10),
                FinalPl03: Dec(11), FinalPl05: Dec(12), Reason: Str(13),
                ReviewedBy: r.GetString(14), ReviewedAt: r.GetDateTime(15));
            map[d.LineId] = d;
        }
        return map;
    }

    public async Task UpsertAsync(IReadOnlyList<PartsPriceDecision> decisions, CancellationToken ct)
    {
        if (decisions.Count == 0) return;
        const string sql = """
            INSERT INTO public.parts_price_review (
                line_id, document_id, status,
                unit_price_foreign, discount_pct, currency, forex_rate, cif_tzs,
                pl01, formula_pl03, formula_pl05,
                final_pl03, final_pl05, reason,
                reviewed_by, reviewed_at)
            VALUES (
                @line, @doc, @status,
                @unit, @disc, @cur, @rate, @cif,
                @pl01, @fpl03, @fpl05,
                @pl03, @pl05, @reason,
                @by, @at)
            ON CONFLICT (line_id) DO UPDATE SET
                document_id        = EXCLUDED.document_id,
                status             = EXCLUDED.status,
                unit_price_foreign = EXCLUDED.unit_price_foreign,
                discount_pct       = EXCLUDED.discount_pct,
                currency           = EXCLUDED.currency,
                forex_rate         = EXCLUDED.forex_rate,
                cif_tzs            = EXCLUDED.cif_tzs,
                pl01               = EXCLUDED.pl01,
                formula_pl03       = EXCLUDED.formula_pl03,
                formula_pl05       = EXCLUDED.formula_pl05,
                final_pl03         = EXCLUDED.final_pl03,
                final_pl05         = EXCLUDED.final_pl05,
                reason             = EXCLUDED.reason,
                reviewed_by        = EXCLUDED.reviewed_by,
                reviewed_at        = EXCLUDED.reviewed_at;
            """;
        await using var conn = new NpgsqlConnection(_company.Current.Neon.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var d in decisions)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("line", d.LineId);
            cmd.Parameters.AddWithValue("doc", d.DocumentId);
            cmd.Parameters.AddWithValue("status", d.Status);
            cmd.Parameters.AddWithValue("unit", (object?)d.UnitPriceForeign ?? DBNull.Value);
            cmd.Parameters.AddWithValue("disc", (object?)d.DiscountPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("cur", (object?)d.Currency ?? DBNull.Value);
            cmd.Parameters.AddWithValue("rate", (object?)d.ForexRate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("cif", (object?)d.CifTzs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("pl01", (object?)d.Pl01 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("fpl03", (object?)d.FormulaPl03 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("fpl05", (object?)d.FormulaPl05 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("pl03", (object?)d.FinalPl03 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("pl05", (object?)d.FinalPl05 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("reason", (object?)d.Reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("by", d.ReviewedBy);
            cmd.Parameters.AddWithValue("at", d.ReviewedAt);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid documentId, Guid lineId, CancellationToken ct)
    {
        const string sql = "DELETE FROM public.parts_price_review WHERE document_id = @doc AND line_id = @line;";
        await using var conn = new NpgsqlConnection(_company.Current.Neon.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("doc", documentId);
        cmd.Parameters.AddWithValue("line", lineId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}
