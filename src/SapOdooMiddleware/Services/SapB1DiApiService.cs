using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SAPbobsCOM;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Sap;
using System.Runtime.InteropServices;

namespace SapOdooMiddleware.Services;

// Part: core
//
// This file is one of FOUR partial-class files that together define
// SapB1DiApiService.  They are:
//   * SapB1DiApiService.cs              ← this file (fields, ctor, connection, ping, UDFs, helpers, Dispose)
//   * SapB1DiApiService.SalesOrders.cs  ← sales orders + pick-list atomic refresh
//   * SapB1DiApiService.Accounting.cs   ← invoices + payments + credit memos
//   * SapB1DiApiService.Logistics.cs    ← deliveries + returns + customers + employees + lookups
//
// The split is purely mechanical (to keep individual files under the
// GitHub MCP push-size limit); at compile time the C# compiler sees
// one single class definition exactly as before.

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public partial class SapB1DiApiService : ISapB1Service, IDisposable
{
    private readonly SapB1Settings _settings;
    private readonly ILogger<SapB1DiApiService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private const string SyncDirectionOdooToSap = "O2S";

    // SAP B1 DI API error codes
    private const int ErrorDbServerTypeNotSupported = -119;
    private const int ErrorSboAuthentication = -132;

    private Company? _company;
    private bool _disposed;

    public SapB1DiApiService(
        IOptions<SapB1Settings> settings,
        ILogger<SapB1DiApiService> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        _logger.LogInformation(
            "🔧 SAP DI API config loaded | Server={Server} | CompanyDB={CompanyDB} | User={User} | DbType={DbType} | LicenseServer={LicenseServer} | SLDServer={SLDServer}",
            _settings.Server,
            _settings.CompanyDb,
            _settings.UserName,
            _settings.DbServerType,
            _settings.LicenseServer,
            _settings.SLDServer);
    }

    // ================================
    // CONNECTION
    // ================================
    private void EnsureConnected()
    {
        if (_company != null && _company.Connected)
            return;

        if (_company != null)
        {
            try { _company.Disconnect(); } catch { /* ignored */ }
            try { Marshal.ReleaseComObject(_company); } catch { /* ignored */ }
            _company = null;
        }

        // --- Pre-connection diagnostics ---
        if (string.IsNullOrWhiteSpace(_settings.LicenseServer))
            _logger.LogWarning(
                "SapB1:LicenseServer is not configured. " +
                "A license server (e.g. \"hostname:30000\") is required by the SAP B1 DI API. " +
                "Connection will likely fail with error -132.");

        _logger.LogInformation(
            "Connecting to SAP B1 DI API — Server={Server}, CompanyDb={CompanyDb}, " +
            "LicenseServer={LicenseServer}, DbServerType={DbServerType}, SLDServer={SLDServer}, UserName={UserName}",
            _settings.Server, _settings.CompanyDb,
            string.IsNullOrWhiteSpace(_settings.LicenseServer) ? "(empty)" : _settings.LicenseServer,
            _settings.DbServerType,
            string.IsNullOrWhiteSpace(_settings.SLDServer) ? "(not set)" : _settings.SLDServer,
            _settings.UserName);

        var company = new Company
        {
            Server = _settings.Server,
            CompanyDB = _settings.CompanyDb,
            UserName = _settings.UserName,
            Password = _settings.Password,
            LicenseServer = _settings.LicenseServer,
            UseTrusted = false,
            language = BoSuppLangs.ln_English
        };

        if (!string.IsNullOrEmpty(_settings.SLDServer))
        {
            try
            {
                company.SLDServer = _settings.SLDServer;
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "SLDServer property not supported by this version of SAPbobsCOM; skipping.");
            }
        }

        int[] candidates = MapDbServerTypeCandidates(_settings.DbServerType);
        int connectResult = -1;
        int errCode = 0;
        string errMsg = string.Empty;

        for (int i = 0; i < candidates.Length; i++)
        {
            company.DbServerType = (BoDataServerTypes)candidates[i];
            _logger.LogInformation(
                "Attempting SAP B1 DI API connection with DbServerType ordinal {Ordinal} (attempt {Attempt}/{Total})",
                candidates[i], i + 1, candidates.Length);

            connectResult = company.Connect();
            if (connectResult == 0)
                break;

            company.GetLastError(out errCode, out errMsg);

            if (errCode == ErrorDbServerTypeNotSupported && i < candidates.Length - 1)
            {
                _logger.LogWarning(
                    "SAP B1 DI API connection failed with DbServerType ordinal {Ordinal} (error {Code}: {Message}). " +
                    "Retrying with next candidate ordinal {NextOrdinal}.",
                    candidates[i], errCode, errMsg, candidates[i + 1]);
                continue;
            }
        }

        if (connectResult != 0)
        {
            Marshal.ReleaseComObject(company);

            var hint = errCode == ErrorSboAuthentication
                ? " Hint for error -132 (SBO user authentication): verify that (1) LicenseServer is set correctly, " +
                  "(2) the installed DI API version matches the SAP B1 server patch level exactly, " +
                  "(3) UserName/Password are valid SAP B1 application credentials (not SQL credentials), " +
                  "and (4) the DI API bitness (x86/x64) matches this application's target platform."
                : string.Empty;

            throw new InvalidOperationException(
                $"SAP B1 DI API connection failed ({errCode}): {errMsg}.{hint}");
        }

        _company = company;
        _logger.LogInformation("✅ SAP B1 DI API connected successfully.");
    }

    // ================================
    // PING
    // ================================
    public async Task<SapB1PingResponse> PingAsync()
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            return new SapB1PingResponse
            {
                Connected = _company!.Connected,
                Server = _settings.Server,
                CompanyDb = _settings.CompanyDb,
                LicenseServer = _settings.LicenseServer,
                SldServer = _settings.SLDServer,
                CompanyName = _company.CompanyName,
                Version = _company.Version.ToString()
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    // ================================
    // UDF SETUP
    // ================================

    /// <summary>
    /// Required UDFs across SAP B1 tables.  Tuple: (Table, Field, Desc, FieldType, Size).
    /// FieldType: 0=db_Alpha, 1=db_Memo, 2=db_Numeric, 3=db_Date, 4=db_Float.
    /// </summary>
    private static readonly (string Table, string Name, string Desc, int Type, int Size)[] RequiredUdfs =
    [
        ("OSLP", "OdooEmployeeId",    "Odoo Employee ID",     0, 20),

        ("OCRD", "OdooCustomerId",    "Odoo Customer ID",     0, 20),
        ("OCRD", "Odoo_LastSync",     "Last Odoo Sync Date",  3,  0),
        ("OCRD", "Odoo_SyncDir",      "Odoo Sync Direction",  0, 10),

        ("ORDR", "Odoo_SO_ID",        "Odoo Sales Order ID",  0, 50),
        ("ORDR", "Odoo_Delivery_ID",  "Odoo Delivery ID",     0, 50),
        ("ORDR", "Odoo_LastSync",     "Last Odoo Sync Date",  3,  0),
        ("ORDR", "Odoo_SyncDir",      "Odoo Sync Direction",  0, 10),

        ("OINV", "Odoo_Invoice_ID",   "Odoo Invoice ID",      0, 50),
        ("OINV", "Odoo_SO_ID",        "Odoo Sales Order ID",  0, 50),
        ("OINV", "Odoo_LastSync",     "Last Odoo Sync Date",  3,  0),
        ("OINV", "Odoo_SyncDir",      "Odoo Sync Direction",  0, 10),
    ];

    public async Task<List<string>> EnsureUdfsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            var results = new List<string>();

            foreach (var udf in RequiredUdfs)
            {
                string fullName = $"U_{udf.Name}";
                try
                {
                    var udfMD = (UserFieldsMD)_company!.GetBusinessObject(BoObjectTypes.oUserFields);

                    udfMD.TableName = udf.Table;
                    udfMD.Name = udf.Name;
                    udfMD.Description = udf.Desc;
                    udfMD.Type = (BoFieldTypes)udf.Type;
                    if (udf.Size > 0)
                        udfMD.Size = udf.Size;

                    int result = udfMD.Add();

                    if (result == 0)
                    {
                        results.Add($"CREATED: {udf.Table}.{fullName} ({udf.Desc})");
                        _logger.LogInformation(
                            "UDF created: {Table}.{Field} ({Desc})",
                            udf.Table, fullName, udf.Desc);
                    }
                    else
                    {
                        _company.GetLastError(out int errCode, out string errMsg);
                        if (errMsg.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                            || errCode == -1120
                            || errCode == -5002)
                        {
                            results.Add($"EXISTS: {udf.Table}.{fullName}");
                            _logger.LogDebug(
                                "UDF already exists: {Table}.{Field}",
                                udf.Table, fullName);
                        }
                        else
                        {
                            results.Add($"ERROR: {udf.Table}.{fullName} — {errCode}: {errMsg}");
                            _logger.LogWarning(
                                "Failed to create UDF {Table}.{Field}: {ErrCode} {ErrMsg}",
                                udf.Table, fullName, errCode, errMsg);
                        }
                    }

                    Marshal.ReleaseComObject(udfMD);
                }
                catch (Exception ex)
                {
                    results.Add($"ERROR: {udf.Table}.{fullName} — {ex.Message}");
                    _logger.LogWarning(ex,
                        "Exception creating UDF {Table}.{Field}",
                        udf.Table, fullName);
                }
            }

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ================================
    // SHARED HELPERS
    // ================================

    /// <summary>
    /// Returns the warehouse code to use for a Sales Order line.  Uses
    /// <paramref name="requestedCode"/> when non-empty; otherwise falls back to
    /// <paramref name="defaultCode"/>.
    /// </summary>
    internal static string ResolveWarehouseCode(string? requestedCode, string defaultCode) =>
        !string.IsNullOrEmpty(requestedCode) ? requestedCode : defaultCode;

    /// <summary>
    /// Maps a string DbServerType (e.g. "dst_MSSQL2019") to one or more SAPbobsCOM
    /// enum ordinal candidates.  Different SAPbobsCOM versions assign different
    /// ordinals to the same logical server type so multiple values may be returned;
    /// the first is the most common mapping, subsequent values are alternatives
    /// tried on error -119 ("Database server type not supported").  Accepts values
    /// with or without the "dst_" prefix, case-insensitive.
    /// </summary>
    internal static int[] MapDbServerTypeCandidates(string dbServerType)
    {
        var normalized = (dbServerType ?? "").Trim().ToUpperInvariant();
        if (normalized.StartsWith("DST_"))
            normalized = normalized["DST_".Length..];

        return normalized switch
        {
            "MSSQL"     => [1],
            "MSSQL2005" => [4],
            "MSSQL2008" => [5, 6],
            "MSSQL2012" => [6, 7],
            "MSSQL2014" => [7, 8],
            "MSSQL2016" => [8, 10],
            "MSSQL2017" => [9, 15],
            "MSSQL2019" => [10, 16],
            "HANADB"    => [11, 9],
            _ => throw new InvalidOperationException(
                $"Unrecognized DbServerType '{dbServerType}'. " +
                "Supported values: MSSQL, MSSQL2005, MSSQL2008, MSSQL2012, MSSQL2014, " +
                "MSSQL2016, MSSQL2017, MSSQL2019, HANADB (with or without 'dst_' prefix).")
        };
    }

    /// <summary>
    /// Attempts to set a User-Defined Field (UDF) on the given <paramref name="userFields"/>
    /// object.  Logs a warning (instead of throwing) when the field does not exist in the
    /// SAP B1 schema.  Returns <c>true</c> on success, <c>false</c> otherwise.
    /// </summary>
    private bool TrySetUserField(UserFields userFields, string fieldName, object value, string context)
    {
        try
        {
            userFields.Fields.Item(fieldName).Value = value;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to set UDF '{FieldName}' on {Context} — field may not exist in this SAP B1 schema.",
                fieldName, context);
            return false;
        }
    }

    // ================================
    // DISPOSE
    // ================================
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_company != null)
        {
            try
            {
                if (_company.Connected)
                    _company.Disconnect();
            }
            catch { }

            try { Marshal.ReleaseComObject(_company); }
            catch { }

            _company = null;
        }

        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}
