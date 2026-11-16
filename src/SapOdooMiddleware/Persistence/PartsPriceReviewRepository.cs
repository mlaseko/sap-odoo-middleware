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
}
