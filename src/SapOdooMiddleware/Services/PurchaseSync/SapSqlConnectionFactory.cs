using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Services.PurchaseSync;

/// <summary>
/// Opens direct SQL Server connections to the Lubes SAP company database for READS (and for the
/// UDF routine's session check). Uses <c>PurchaseSync:SapSqlConnectionString</c> when set, else the
/// same construction as the Lubes inventory forecast report: <c>SapB1:Server</c> / <c>CompanyDb</c>
/// with the SQL login <c>DbUserName</c>/<c>DbPassword</c> (falling back to the DI-API user).
/// </summary>
public sealed class SapSqlConnectionFactory
{
    public const string ApplicationName = "SapOdooMiddleware";

    private readonly IOptionsMonitor<PurchaseSyncSettings> _purchase;
    private readonly IOptionsMonitor<SapB1Settings> _sap;

    public SapSqlConnectionFactory(
        IOptionsMonitor<PurchaseSyncSettings> purchase,
        IOptionsMonitor<SapB1Settings> sap)
    {
        _purchase = purchase;
        _sap = sap;
    }

    public string ConnectionString
    {
        get
        {
            var explicitCs = _purchase.CurrentValue.SapSqlConnectionString;
            if (!string.IsNullOrWhiteSpace(explicitCs))
            {
                var b = new SqlConnectionStringBuilder(explicitCs);
                if (string.IsNullOrWhiteSpace(b.ApplicationName) || b.ApplicationName == ".Net SqlClient Data Provider")
                    b.ApplicationName = ApplicationName;
                return b.ConnectionString;
            }

            var sap = _sap.CurrentValue;
            return new SqlConnectionStringBuilder
            {
                DataSource = sap.Server,
                InitialCatalog = sap.CompanyDb,
                UserID = string.IsNullOrWhiteSpace(sap.DbUserName) ? sap.UserName : sap.DbUserName,
                Password = string.IsNullOrWhiteSpace(sap.DbUserName) ? sap.Password : sap.DbPassword,
                TrustServerCertificate = true,
                ApplicationName = ApplicationName,
            }.ConnectionString;
        }
    }

    public string CompanyDb => new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
