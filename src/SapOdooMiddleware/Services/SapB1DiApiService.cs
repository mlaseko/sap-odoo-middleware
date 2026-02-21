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

    private dynamic? _company;
    private bool _disposed;

    public SapB1DiApiService(IOptions<SapB1Settings> settings, ILogger<SapB1DiApiService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<SapSalesOrderResponse> CreateSalesOrderAsync(SapSalesOrderRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            var company = _company!;

            // --- Create Sales Order ---
            dynamic order = company.GetBusinessObject(17); // oOrders = 17
            order.CardCode = request.CardCode;
            order.NumAtCard = request.OdooSoRef; // Odoo SO ref on header

            if (request.DocDate.HasValue)
                order.DocDate = request.DocDate.Value;

            if (request.DocDueDate.HasValue)
                order.DocDueDate = request.DocDueDate.Value;

            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];

                if (i > 0)
                    order.Lines.Add();

                order.Lines.ItemCode = line.ItemCode;
                order.Lines.Quantity = line.Quantity;

                if (line.UnitPrice.HasValue)
                    order.Lines.UnitPrice = line.UnitPrice.Value;

                if (!string.IsNullOrEmpty(line.WarehouseCode))
                    order.Lines.WarehouseCode = line.WarehouseCode;

                if (!string.IsNullOrEmpty(line.OdooLineRef))
                    order.Lines.FreeText = line.OdooLineRef;
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
                "Created SAP Sales Order DocEntry={DocEntry} DocNum={DocNum} for Odoo ref {OdooSoRef}",
                docEntry, docNum, request.OdooSoRef);

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
                OdooSoRef = request.OdooSoRef,
                PickListEntry = pickListEntry
            };
        }
        finally
        {
            _lock.Release();
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

        _logger.LogInformation("Connecting to SAP B1 DI API: Server={Server}, CompanyDb={CompanyDb}",
            _settings.Server, _settings.CompanyDb);

        var companyType = Type.GetTypeFromProgID("SAPbobsCOM.Company")
            ?? throw new InvalidOperationException(
                "SAPbobsCOM.Company COM class not found. Ensure SAP B1 DI API is installed.");

        dynamic company = Activator.CreateInstance(companyType)!;
        company.Server = _settings.Server;
        company.CompanyDB = _settings.CompanyDb;
        company.UserName = _settings.UserName;
        company.Password = _settings.Password;
        company.LicenseServer = _settings.LicenseServer;

        // Map DbServerType string to enum value
        company.DbServerType = MapDbServerType(_settings.DbServerType);

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

        int connectResult = company.Connect();
        if (connectResult != 0)
        {
            int errCode = company.GetLastErrorCode();
            string errMsg = company.GetLastErrorDescription();
            Marshal.ReleaseComObject(company);
            throw new InvalidOperationException($"SAP B1 DI API connection failed ({errCode}): {errMsg}");
        }

        _company = company;
        _logger.LogInformation("Connected to SAP B1 DI API successfully.");
    }

    /// <summary>
    /// Maps a string DbServerType (e.g. "dst_MSSQL2019") to the SAPbobsCOM enum ordinal value.
    /// </summary>
    private static int MapDbServerType(string dbServerType) => dbServerType switch
    {
        "dst_MSSQL" => 1,
        "dst_MSSQL2005" => 4,
        "dst_MSSQL2008" => 5,
        "dst_MSSQL2012" => 6,
        "dst_MSSQL2014" => 7,
        "dst_MSSQL2016" => 8,
        "dst_MSSQL2017" => 9,
        "dst_MSSQL2019" => 10,
        "dst_HANADB" => 11,
        _ => 10 // default to MSSQL2019
    };

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
