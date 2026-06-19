using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>One verification line in the Autohub SAP pre-flight result.</summary>
public sealed record SapSetupCheck(string Name, bool Ok, string Detail);

public sealed record AutohubSapSetupResult(
    bool Connected, string Company, bool AllOk, IReadOnlyList<SapSetupCheck> Checks, string? Error = null);

/// <summary>
/// Read-only pre-flight check that the <b>Autohub</b> SAP company (Companies:Autohub:SapB1, e.g.
/// "Molas Live 2021") has the master-data the Autohub item-create assumes: ≥5 price lists (so PL01/03/05
/// land on the right lists), item groups, the OITM UDFs (U_Article_No/Description/FitForAuto/ImageUrl),
/// the O1/I1 VAT groups, and UoM group 1 ("Packing Units"). Connects via plain SqlClient (the Autohub
/// company is MSSQL) — no DI-API license seat consumed. Surfaces problems before a bulk-create instead of
/// per-line create_failed.
/// </summary>
public sealed class AutohubSapSetupVerifier
{
    private readonly CompaniesOptions _companies;
    private readonly ILogger<AutohubSapSetupVerifier> _logger;

    public AutohubSapSetupVerifier(IOptions<CompaniesOptions> companies, ILogger<AutohubSapSetupVerifier> logger)
    {
        _companies = companies.Value;
        _logger = logger;
    }

    public async Task<AutohubSapSetupResult> VerifyAsync(CancellationToken ct)
    {
        if (!_companies.Companies.TryGetValue(CompanyContext.AutohubKey, out var cfg) || cfg.SapB1 is null
            || string.IsNullOrWhiteSpace(cfg.SapB1.Server) || string.IsNullOrWhiteSpace(cfg.SapB1.CompanyDb))
            return new AutohubSapSetupResult(false, "(not configured)", false, Array.Empty<SapSetupCheck>(),
                "Companies:Autohub:SapB1 (Server/CompanyDb) is not configured.");

        var sap = cfg.SapB1;
        if (!sap.DbServerType.Contains("MSSQL", StringComparison.OrdinalIgnoreCase))
            return new AutohubSapSetupResult(false, sap.CompanyDb, false, Array.Empty<SapSetupCheck>(),
                $"Verifier supports MSSQL only (DbServerType={sap.DbServerType}).");

        var connStr = new SqlConnectionStringBuilder
        {
            DataSource = sap.Server,
            InitialCatalog = sap.CompanyDb,
            UserID = sap.UserName,
            Password = sap.Password,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
        }.ConnectionString;

        var checks = new List<SapSetupCheck>();
        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);

            // 1) Price lists — item-create writes to collection index 0/2/4 (PL01/PL03/PL05), so ≥5 needed.
            var priceLists = await ReadRowsAsync(conn, "SELECT ListNum, ListName FROM OPLN ORDER BY ListNum", ct);
            var plNames = priceLists.Select(r => $"{r[0]}:{r[1]}").ToList();
            checks.Add(new SapSetupCheck("Price lists (need ≥5; PL01/PL03/PL05 = Cost/Retail/Wholesale)",
                priceLists.Count >= 5,
                priceLists.Count == 0 ? "none found" : string.Join(", ", plNames)));

            // 2) Item groups — DGX's suggested_itms_grp_cod must be a valid OITB group.
            var groups = await ReadRowsAsync(conn, "SELECT ItmsGrpCod, ItmsGrpNam FROM OITB ORDER BY ItmsGrpCod", ct);
            checks.Add(new SapSetupCheck("Item groups (OITB)", groups.Count > 0,
                groups.Count == 0 ? "none found" : $"{groups.Count} groups: " +
                    string.Join(", ", groups.Take(20).Select(r => $"{r[0]}:{r[1]}")) + (groups.Count > 20 ? " …" : "")));

            // 3) OITM UDFs — set on create (U_Article_No is also the Tier-2 match key). CUFD AliasID has no U_.
            var wanted = new[] { "Article_No", "Description", "FitForAuto", "ImageUrl" };
            var present = (await ReadRowsAsync(conn,
                    "SELECT AliasID FROM CUFD WHERE TableID = 'OITM' AND AliasID IN ('Article_No','Description','FitForAuto','ImageUrl')", ct))
                .Select(r => (string)r[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = wanted.Where(w => !present.Contains(w)).ToList();
            checks.Add(new SapSetupCheck("OITM user fields (U_Article_No/U_Description/U_FitForAuto/U_ImageUrl)",
                missing.Count == 0,
                missing.Count == 0 ? "all present" : "MISSING: " + string.Join(", ", missing.Select(m => "U_" + m))));

            // 4) VAT groups — O1 (sales) and I1 (purchase) referenced on create.
            var vat = (await ReadRowsAsync(conn, "SELECT Code FROM OVTG WHERE Code IN ('O1','I1')", ct))
                .Select(r => (string)r[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var vatMissing = new[] { "O1", "I1" }.Where(c => !vat.Contains(c)).ToList();
            checks.Add(new SapSetupCheck("VAT groups O1 / I1", vatMissing.Count == 0,
                vatMissing.Count == 0 ? "both present" : "MISSING: " + string.Join(", ", vatMissing)));

            // 5) UoM group 1 ("Packing Units") — items are created in it.
            var uom = await ReadRowsAsync(conn, "SELECT UgpEntry, UgpName FROM OUGP WHERE UgpEntry = 1", ct);
            checks.Add(new SapSetupCheck("UoM group entry 1 (Packing Units)", uom.Count > 0,
                uom.Count == 0 ? "not found" : $"UgpEntry 1 = {uom[0][1]}"));

            return new AutohubSapSetupResult(true, sap.CompanyDb, checks.All(c => c.Ok), checks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Autohub SAP setup verify failed for {Company}.", sap.CompanyDb);
            return new AutohubSapSetupResult(false, sap.CompanyDb, false, checks, ex.Message);
        }
    }

    private static async Task<List<object[]>> ReadRowsAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 20 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object[]>();
        while (await r.ReadAsync(ct))
        {
            var vals = new object[r.FieldCount];
            r.GetValues(vals);
            rows.Add(vals);
        }
        return rows;
    }
}
