using Npgsql;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Persistence;

/// <summary>One recorded pricing override (see pricing_overrides).</summary>
public sealed record PricingOverrideRecord(
    string ItemCode, string? Brand, decimal? Cif,
    decimal? FormulaPl01, decimal? FormulaPl03, decimal? FormulaPl05,
    decimal? AppliedPl01, decimal? AppliedPl03, decimal? AppliedPl05,
    string Source, string? Reason, string? RequestedBy);

public interface IPricingOverrideRepository
{
    /// <summary>
    /// Append one row to pricing_overrides — the audit trail behind every reviewer price
    /// decision. The VIKA ratios drifted for months because hand corrections left no trace;
    /// with this table the next ratio review is a query, not an investigation.
    /// </summary>
    Task RecordAsync(PricingOverrideRecord record, CancellationToken ct);
}

/// <summary>
/// Writes pricing_overrides in parts_catalog. Connection per-tenant via ICompanyContext
/// (/api/sap resolves to the Autohub tenant). Append-only; nothing here reads it back —
/// the pricing team queries it directly.
/// </summary>
public sealed class PricingOverrideRepository : IPricingOverrideRepository
{
    private readonly ICompanyContext _company;
    public PricingOverrideRepository(ICompanyContext company) => _company = company;

    private string ConnectionString => _company.Current.Neon.ConnectionString;

    public async Task RecordAsync(PricingOverrideRecord r, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO pricing_overrides
              (item_code, brand, cif, formula_pl01, formula_pl03, formula_pl05,
               applied_pl01, applied_pl03, applied_pl05, pl03_difference,
               source, reason, requested_by)
            VALUES
              (@code, @brand, @cif, @fPl01, @fPl03, @fPl05,
               @aPl01, @aPl03, @aPl05, @diff,
               @source, @reason, @user);
            """;

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("code", r.ItemCode);
        cmd.Parameters.AddWithValue("brand", (object?)r.Brand ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cif", (object?)r.Cif ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fPl01", (object?)r.FormulaPl01 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fPl03", (object?)r.FormulaPl03 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fPl05", (object?)r.FormulaPl05 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("aPl01", (object?)r.AppliedPl01 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("aPl03", (object?)r.AppliedPl03 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("aPl05", (object?)r.AppliedPl05 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("diff",
            r.AppliedPl03 is { } a && r.FormulaPl03 is { } f ? a - f : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("source", r.Source);
        cmd.Parameters.AddWithValue("reason", (object?)r.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("user", (object?)r.RequestedBy ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
