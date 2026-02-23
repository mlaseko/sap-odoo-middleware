using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SAPbobsCOM;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Sap;
using System.Runtime.InteropServices;

namespace SapOdooMiddleware.Services;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class SapB1DiApiService : ISapB1Service, IDisposable
{
    private readonly SapB1Settings _settings;
    private readonly ILogger<SapB1DiApiService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Company? _company;
    private bool _disposed;

    public SapB1DiApiService(
        IOptions<SapB1Settings> settings,
        ILogger<SapB1DiApiService> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        _logger.LogInformation(
            "🔧 SAP DI API config loaded | Server={Server} | CompanyDB={CompanyDB} | User={User} | DbType={DbType}",
            _settings.Server,
            _settings.CompanyDb,
            _settings.UserName,
            _settings.DbServerType);
    }

    // ================================
    // CONNECTION
    // ================================
    private void EnsureConnected()
    {
        if (_company != null && _company.Connected)
            return;

        _company?.Disconnect();
        _company = null;

        var dbTypeRaw = _settings.DbServerType?.Trim() ?? "";

        if (dbTypeRaw.StartsWith("dst_", StringComparison.OrdinalIgnoreCase))
            dbTypeRaw = dbTypeRaw.Substring(4);

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
        if (!Enum.TryParse($"dst_{dbTypeRaw}", true, out BoDataServerTypes dbType))
        {
            throw new InvalidOperationException(
                $"Invalid DbServerType '{_settings.DbServerType}'. Example: MSSQL2016");
        }

        var company = new Company
        {
            Server = _settings.Server,
            CompanyDB = _settings.CompanyDb,
            UserName = _settings.UserName,
            Password = _settings.Password,
            LicenseServer = _settings.LicenseServer,
            SLDServer = _settings.SLDServer,
            UseTrusted = false,
            language = BoSuppLangs.ln_English,
            DbServerType = dbType
        };

        _logger.LogInformation(
            "Connecting to SAP B1 DI API — Server={Server}, DB={CompanyDb}, Type={DbType}",
            _settings.Server,
            _settings.CompanyDb,
            dbType);

        int result = company.Connect();

        if (result != 0)
        {
            company.GetLastError(out int errCode, out string errMsg);

            Marshal.ReleaseComObject(company);

            throw new InvalidOperationException(
                $"SAP B1 DI API connection failed ({errCode}): {errMsg}");
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
    // SALES ORDER
    // ================================
    public async Task<SapSalesOrderResponse> CreateSalesOrderAsync(SapSalesOrderRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            _logger.LogInformation(
                "AutoCreatePickList={AutoCreatePickList}",
                _settings.AutoCreatePickList);

            var order = (Documents)_company!.GetBusinessObject(BoObjectTypes.oOrders);

            order.CardCode = request.CardCode;
            order.NumAtCard = request.ResolvedSoId;

            if (request.DocDate.HasValue)
                order.DocDate = request.DocDate.Value;

            if (request.DocDueDate.HasValue)
                order.DocDueDate = request.DocDueDate.Value;

            bool udfHeaderSet = TrySetUserField(order.UserFields, "U_Odoo_SO_ID", request.ResolvedSoId, "SO header");
            if (udfHeaderSet)
                _logger.LogDebug("UDF U_Odoo_SO_ID set to '{Value}' on SO header.", request.ResolvedSoId);

            for (int i = 0; i < request.Lines.Count; i++)
            {
                if (i > 0)
                    order.Lines.Add();

                var line = request.Lines[i];

                // Display value for logging only — "(default)" when no warehouse was specified.
                var warehouseForLogging = line.WarehouseCode ?? "(default)";

                _logger.LogDebug(
                    "Line[{Index}] ItemCode={ItemCode}, Qty={Qty}, UnitPrice={UnitPrice}, GrossBuyPr={GrossBuyPr}, Warehouse={Warehouse}",
                    i, line.ItemCode, line.Quantity, line.UnitPrice, line.GrossBuyPr, warehouseForLogging);

                order.Lines.ItemCode = line.ItemCode;
                order.Lines.Quantity = line.Quantity;
                order.Lines.UnitPrice = line.UnitPrice;

                if (line.GrossBuyPr.HasValue)
                    order.Lines.GrossBuyPr = line.GrossBuyPr.Value;

                if (!string.IsNullOrEmpty(line.WarehouseCode))
                    order.Lines.WarehouseCode = line.WarehouseCode;

                if (!string.IsNullOrEmpty(line.UOdooSoLineId))
                    TrySetUserField(order.Lines.UserFields, "U_Odoo_SOLine_ID", line.UOdooSoLineId, $"line[{i}]");

                if (!string.IsNullOrEmpty(line.UOdooMoveId))
                    TrySetUserField(order.Lines.UserFields, "U_Odoo_Move_ID", line.UOdooMoveId, $"line[{i}]");

                if (!string.IsNullOrEmpty(line.UOdooDeliveryId))
                    TrySetUserField(order.Lines.UserFields, "U_Odoo_Delivery_ID", line.UOdooDeliveryId, $"line[{i}]");
            }

            int result = order.Add();

            if (result != 0)
            {
                _company.GetLastError(out int errCode, out string errMsg);
                Marshal.ReleaseComObject(order);

                throw new InvalidOperationException(
                    $"SAP DI API error {errCode}: {errMsg}");
            }

            int docEntry = int.Parse(_company.GetNewObjectKey());

            order.GetByKey(docEntry);
            int docNum = order.DocNum;

            Marshal.ReleaseComObject(order);

            var response = new SapSalesOrderResponse
            {
                DocEntry = docEntry,
                DocNum = docNum,
                UOdooSoId = request.ResolvedSoId
            };

            if (_settings.AutoCreatePickList)
            {
                _logger.LogInformation(
                    "AutoCreatePickList enabled — creating pick list for DocEntry={DocEntry}", docEntry);

                try
                {
                    var pickList = (PickLists)_company!.GetBusinessObject(BoObjectTypes.oPickLists);
                    pickList.PickDate = DateTime.Now;

                    for (int i = 0; i < request.Lines.Count; i++)
                    {
                        if (i > 0) pickList.Lines.Add();
                        pickList.Lines.DocumentEntry = docEntry;
                        pickList.Lines.DocumentRowID = i;
                        pickList.Lines.ReleasedQuantity = request.Lines[i].Quantity;
                    }

                    int plResult = pickList.Add();

                    if (plResult != 0)
                    {
                        _company!.GetLastError(out int plErrCode, out string plErrMsg);
                        _logger.LogWarning(
                            "Pick list creation failed (ErrCode={ErrCode}): {ErrMsg}", plErrCode, plErrMsg);
                    }
                    else
                    {
                        int pickListEntry = int.Parse(_company!.GetNewObjectKey());
                        _logger.LogInformation(
                            "✅ Pick list created: AbsEntry={PickListEntry}", pickListEntry);
                        response.PickListEntry = pickListEntry;
                    }

                    Marshal.ReleaseComObject(pickList);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Pick list creation threw an exception — continuing without pick list.");
                }
            }
            else
            {
                _logger.LogDebug("AutoCreatePickList disabled — skipping pick list creation.");
            }

            _logger.LogInformation(
                "PickListEntry={PickListEntry} (created={Created})",
                response.PickListEntry, response.PickListEntry.HasValue);

            return response;
        }
        finally
        {
            _lock.Release();
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

    /// <summary>
    /// Attempts to set a User-Defined Field (UDF) on the given <paramref name="userFields"/> object.
    /// Logs a warning (instead of throwing) when the field does not exist in the SAP B1 schema.
    /// </summary>
    /// <returns><c>true</c> if the field was set successfully; <c>false</c> otherwise.</returns>
    private bool TrySetUserField(UserFields userFields, string fieldName, string value, string context)
    {
        try
        {
            userFields.Fields.Item(fieldName).Value = value;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to set UDF '{FieldName}' on {Context} — field may not exist in this SAP B1 schema.",
                fieldName, context);
            return false;
        }
    }

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