using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Sap;

namespace SapOdooMiddleware.Services;

/// <summary>
/// SAP B1 DI API service that creates Sales Orders and Pick Lists via COM interop.
/// Requires SAPbobsCOM DI API installed on the host machine (Windows + SAP B1 client/DI API).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class SapB1DiApiService : ISapB1Service, IDisposable
{
    private readonly SapB1Settings _settings;
    private readonly ILogger<SapB1DiApiService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // BoSuppLangs.ln_English = 8 (SAPbobsCOM language enum value)
    private const int LanguageEnglish = 8;

    // SAP B1 DI API error codes
    private const int ErrorDbServerTypeNotSupported = -119;
    private const int ErrorSboAuthentication = -132;

    private dynamic? _company;
    private bool _disposed;

    public SapB1DiApiService(IOptions<SapB1Settings> settings, ILogger<SapB1DiApiService> logger)
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

    public async Task<SapSalesOrderResponse> CreateSalesOrderAsync(SapSalesOrderRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            var company = _company!;
            var soId = request.ResolvedSoId;

            // --- Create Sales Order ---
            dynamic order = company.GetBusinessObject(17); // oOrders = 17
            order.CardCode = request.CardCode;
            order.NumAtCard = soId; // Odoo SO identifier on header (NumAtCard)

            if (request.DocDate.HasValue)
                order.DocDate = request.DocDate.Value;

            if (request.DocDueDate.HasValue)
                order.DocDueDate = request.DocDueDate.Value;

            // Set header UDF U_Odoo_SO_ID
            TrySetUserField(order, "U_Odoo_SO_ID", soId);

            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];

                if (i > 0)
                    order.Lines.Add();

                order.Lines.ItemCode = line.ItemCode;
                order.Lines.Quantity = line.Quantity;
                order.Lines.UnitPrice = line.UnitPrice;

                if (line.GrossBuyPr.HasValue)
                    order.Lines.GrossBuyPr = line.GrossBuyPr.Value;

                var warehouseCode = ResolveWarehouseCode(line.WarehouseCode, _settings.DefaultWarehouseCode);
                if (string.IsNullOrEmpty(line.WarehouseCode))
                    _logger.LogInformation(
                        "No WarehouseCode on line {LineIndex} (item {ItemCode}) for Odoo ref {SoId} — defaulting to '{DefaultWarehouse}'",
                        i, line.ItemCode, soId, warehouseCode);
                order.Lines.WarehouseCode = warehouseCode;

                // Set line UDFs
                if (!string.IsNullOrEmpty(line.UOdooSoLineId))
                    TrySetLineUserField(order, "U_Odoo_SOLine_ID", line.UOdooSoLineId);

                if (!string.IsNullOrEmpty(line.UOdooMoveId))
                    TrySetLineUserField(order, "U_Odoo_Move_ID", line.UOdooMoveId);

                if (!string.IsNullOrEmpty(line.UOdooDeliveryId))
                    TrySetLineUserField(order, "U_Odoo_Delivery_ID", line.UOdooDeliveryId);
            }

            int result = order.Add();
            if (result != 0)
            {
                int errCode = company.GetLastErrorCode();
                string errMsg = company.GetLastErrorDescription();
                _logger.LogError("SAP DI API error creating SO: {Code} - {Message}", errCode, errMsg);
                throw new InvalidOperationException($"SAP DI API error {errCode}: {errMsg}");
            }

            string docEntryStr = company.GetNewObjectKey();
            int docEntry = int.Parse(docEntryStr);

            // Retrieve DocNum
            order.GetByKey(docEntry);
            int docNum = (int)order.DocNum;

            _logger.LogInformation(
                "Created SAP Sales Order DocEntry={DocEntry} DocNum={DocNum} for Odoo ref {UOdooSoId}",
                docEntry, docNum, soId);

            // --- Optionally create Pick List ---
            int? pickListEntry = null;
            if (_settings.AutoCreatePickList)
            {
                pickListEntry = CreatePickList(company, docEntry, request.Lines.Count);
            }

            Marshal.ReleaseComObject(order);

            return new SapSalesOrderResponse
            {
                DocEntry = docEntry,
                DocNum = docNum,
                UOdooSoId = soId,
                PickListEntry = pickListEntry
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Attempts to set a user-defined field (UDF) on the document header.
    /// Logs a warning and continues if the UDF does not exist in this SAP B1 system.
    /// </summary>
    private void TrySetUserField(dynamic obj, string fieldName, string value)
    {
        try
        {
            obj.UserFields.Fields.Item(fieldName).Value = value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UDF '{FieldName}' not found on document header — skipping.", fieldName);
        }
    }

    /// <summary>
    /// Attempts to set a user-defined field (UDF) on the current document line.
    /// Logs a warning and continues if the UDF does not exist in this SAP B1 system.
    /// </summary>
    private void TrySetLineUserField(dynamic obj, string fieldName, string value)
    {
        try
        {
            obj.Lines.UserFields.Fields.Item(fieldName).Value = value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UDF '{FieldName}' not found on document line — skipping.", fieldName);
        }
    }

    public async Task<SapB1PingResponse> PingAsync()
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            var company = _company!;

            string? companyName = null;
            string? version = null;

            try { companyName = (string)company.CompanyName; }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to read CompanyName from SAP B1 company object."); }

            try { version = (string)company.Version; }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to read Version from SAP B1 company object."); }

            return new SapB1PingResponse
            {
                Connected = true,
                Server = _settings.Server,
                CompanyDb = _settings.CompanyDb,
                LicenseServer = _settings.LicenseServer,
                SldServer = _settings.SLDServer,
                CompanyName = companyName,
                Version = version
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Creates a Pick List (Pick &amp; Pack) for the given Sales Order.
    /// See: SAPbobsCOM PickLists / PickLists_Lines documentation.
    /// </summary>
    private int? CreatePickList(dynamic company, int orderDocEntry, int lineCount)
    {
        try
        {
            dynamic pickList = company.GetBusinessObject(310); // oPickLists = 310

            for (int i = 0; i < lineCount; i++)
            {
                if (i > 0)
                    pickList.Lines.Add();

                pickList.Lines.OrderEntry = orderDocEntry;
                pickList.Lines.OrderRowID = i; // zero-based line index
            }

            int result = pickList.Add();
            if (result != 0)
            {
                int errCode = company.GetLastErrorCode();
                string errMsg = company.GetLastErrorDescription();
                _logger.LogWarning("SAP DI API error creating PickList: {Code} - {Message}", errCode, errMsg);
                Marshal.ReleaseComObject(pickList);
                return null;
            }

            string absEntryStr = company.GetNewObjectKey();
            int absEntry = int.Parse(absEntryStr);
            _logger.LogInformation("Created SAP Pick List AbsEntry={AbsEntry} for SO DocEntry={DocEntry}", absEntry, orderDocEntry);

            Marshal.ReleaseComObject(pickList);
            return absEntry;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create Pick List for SO DocEntry={DocEntry}", orderDocEntry);
            return null;
        }
    }

    private void EnsureConnected()
    {
        if (_company != null)
        {
            if ((bool)_company.Connected)
                return;

            // Disconnected — dispose and reconnect
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

        var companyType = Type.GetTypeFromProgID("SAPbobsCOM.Company")
            ?? throw new InvalidOperationException(
                "SAPbobsCOM.Company COM class not found. Ensure the SAP B1 DI API is installed and its " +
                "version matches the SAP B1 server patch level (e.g. 10.00.110).");

        dynamic company = Activator.CreateInstance(companyType)!;
        company.Server = _settings.Server;
        company.CompanyDB = _settings.CompanyDb;
        company.UserName = _settings.UserName;
        company.Password = _settings.Password;
        company.LicenseServer = _settings.LicenseServer;
        company.UseTrusted = false;
        company.Language = LanguageEnglish;

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

        // Try each candidate enum value for DbServerType; different SAPbobsCOM
        // versions use different ordinals for the same logical server type.
        int[] candidates = MapDbServerTypeCandidates(_settings.DbServerType);
        int connectResult = -1;
        int errCode = 0;
        string errMsg = string.Empty;

        for (int i = 0; i < candidates.Length; i++)
        {
            company.DbServerType = candidates[i];
            _logger.LogInformation(
                "Attempting SAP B1 DI API connection with DbServerType ordinal {Ordinal} (attempt {Attempt}/{Total})",
                candidates[i], i + 1, candidates.Length);

            connectResult = company.Connect();
            if (connectResult == 0)
                break;

            errCode = company.GetLastErrorCode();
            errMsg = company.GetLastErrorDescription();

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
        _logger.LogInformation("Connected to SAP B1 DI API successfully.");
    }

    /// <summary>
    /// Returns the warehouse code to use for a Sales Order line.
    /// Uses <paramref name="requestedCode"/> when non-empty; otherwise falls back to
    /// <paramref name="defaultCode"/>.
    /// </summary>
    internal static string ResolveWarehouseCode(string? requestedCode, string defaultCode) =>
        !string.IsNullOrEmpty(requestedCode) ? requestedCode : defaultCode;

    /// <summary>
    /// Maps a string DbServerType (e.g. "dst_MSSQL2019", "MSSQL2019", "MSSQL2016") to one or
    /// more SAPbobsCOM enum ordinal candidates. Different SAPbobsCOM versions assign different
    /// ordinals to the same logical server type, so multiple values may be returned.
    /// The first value is the most common mapping; subsequent values are alternatives tried
    /// when error -119 ("Database server type not supported") is returned.
    /// Accepts values with or without the "dst_" prefix, case-insensitive.
    /// </summary>
    internal static int[] MapDbServerTypeCandidates(string dbServerType)
    {
        // Normalize: trim, uppercase, strip "dst_" prefix if present
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
                $"Supported values: MSSQL, MSSQL2005, MSSQL2008, MSSQL2012, MSSQL2014, MSSQL2016, MSSQL2017, MSSQL2019, HANADB " +
                $"(with or without 'dst_' prefix).")
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_company != null)
        {
            try
            {
                if ((bool)_company.Connected)
                    _company.Disconnect();
            }
            catch { /* ignored */ }

            try { Marshal.ReleaseComObject(_company); }
            catch { /* ignored */ }

            _company = null;
        }

        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}