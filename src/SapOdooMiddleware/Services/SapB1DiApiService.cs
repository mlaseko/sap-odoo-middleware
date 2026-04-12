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

        // Try each candidate enum value for DbServerType; different SAPbobsCOM
        // versions use different ordinals for the same logical server type.
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
    /// Required UDFs across SAP B1 tables.
    /// Each tuple: (TableName, FieldName, Description, FieldType, Size).
    /// FieldType: 0 = db_Alpha, 1 = db_Memo, 2 = db_Numeric, 3 = db_Date, 4 = db_Float
    /// </summary>
    private static readonly (string Table, string Name, string Desc, int Type, int Size)[] RequiredUdfs =
    [
        // OSLP (Sales Employees)
        ("OSLP", "OdooEmployeeId", "Odoo Employee ID", 0, 20),

        // OCRD (Business Partners / Customers)
        ("OCRD", "OdooCustomerId", "Odoo Customer ID", 0, 20),
        ("OCRD", "Odoo_LastSync", "Last Odoo Sync Date", 3, 0),
        ("OCRD", "Odoo_SyncDir", "Odoo Sync Direction", 0, 10),

        // ORDR (Sales Orders)
        ("ORDR", "Odoo_SO_ID", "Odoo Sales Order ID", 0, 50),
        ("ORDR", "Odoo_Delivery_ID", "Odoo Delivery ID", 0, 50),
        ("ORDR", "Odoo_LastSync", "Last Odoo Sync Date", 3, 0),
        ("ORDR", "Odoo_SyncDir", "Odoo Sync Direction", 0, 10),

        // OINV (AR Invoices)
        ("OINV", "Odoo_Invoice_ID", "Odoo Invoice ID", 0, 50),
        ("OINV", "Odoo_SO_ID", "Odoo Sales Order ID", 0, 50),
        ("OINV", "Odoo_LastSync", "Last Odoo Sync Date", 3, 0),
        ("OINV", "Odoo_SyncDir", "Odoo Sync Direction", 0, 10),
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

            if (request.SlpCode.HasValue && request.SlpCode.Value >= 0)
                order.SalesPersonCode = request.SlpCode.Value;

            if (request.DocDate.HasValue)
                order.DocDate = request.DocDate.Value;

            if (request.DocDueDate.HasValue)
                order.DocDueDate = request.DocDueDate.Value;

            bool udfHeaderSet = TrySetUserField(order.UserFields, "U_Odoo_SO_ID", request.ResolvedSoId, "SO header");
            if (udfHeaderSet)
                _logger.LogDebug("UDF U_Odoo_SO_ID set to '{Value}' on SO header.", request.ResolvedSoId);

            var syncDate = DateTime.UtcNow.Date;
            TrySetUserField(order.UserFields, "U_Odoo_LastSync", syncDate, "SO header");
            TrySetUserField(order.UserFields, "U_Odoo_SyncDir", SyncDirectionOdooToSap, "SO header");

            var deliveryId = request.ResolvedDeliveryId;
            _logger.LogInformation(
                "OdooDeliveryId received: '{DeliveryId}' (length={Length})",
                deliveryId ?? "(none)", deliveryId?.Length ?? 0);

            if (!string.IsNullOrEmpty(deliveryId))
            {
                _logger.LogInformation(
                    "Attempting to write UDF U_Odoo_Delivery_ID with value length={Length}",
                    deliveryId.Length);

                bool deliveryIdSet = TrySetUserField(order.UserFields, "U_Odoo_Delivery_ID", deliveryId, "SO header");

                if (!deliveryIdSet)
                {
                    _company!.GetLastError(out int udfErrCode, out string udfErrMsg);
                    Marshal.ReleaseComObject(order);
                    throw new InvalidOperationException(
                        $"Failed to set UDF 'U_Odoo_Delivery_ID' (value length={deliveryId.Length}): " +
                        $"DI API error {udfErrCode}: {udfErrMsg}");
                }

                _logger.LogInformation(
                    "✅ UDF U_Odoo_Delivery_ID set successfully (value length={Length})",
                    deliveryId.Length);
            }

            _logger.LogDebug("UDF U_Odoo_LastSync set to '{Value}' and U_Odoo_SyncDir set to '{SyncDir}' on SO header.", syncDate, SyncDirectionOdooToSap);

            for (int i = 0; i < request.Lines.Count; i++)
            {
                if (i > 0)
                    order.Lines.Add();

                var line = request.Lines[i];

                // Display value for logging only — "(default)" when no warehouse was specified.
                var warehouseForLogging = line.WarehouseCode ?? "(default)";

                _logger.LogDebug(
                    "Line[{Index}] ItemCode={ItemCode}, Qty={Qty}, UnitPrice={UnitPrice}, Warehouse={Warehouse}",
                    i, line.ItemCode, line.Quantity, line.UnitPrice, warehouseForLogging);

                order.Lines.ItemCode = line.ItemCode;
                order.Lines.Quantity = line.Quantity;
                order.Lines.UnitPrice = line.UnitPrice;

                if (!string.IsNullOrEmpty(line.WarehouseCode))
                    order.Lines.WarehouseCode = line.WarehouseCode;

                if (!string.IsNullOrEmpty(line.UOdooSoLineId))
                    TrySetUserField(order.Lines.UserFields, "U_Odoo_SOLine_ID", line.UOdooSoLineId, $"line[{i}]");

                if (!string.IsNullOrEmpty(line.UOdooMoveId))
                    TrySetUserField(order.Lines.UserFields, "U_Odoo_Move_ID", line.UOdooMoveId, $"line[{i}]");
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

            // Capture line info before releasing the COM object (used for pick list linkage).
            var lineCaptures = new List<(int lineNum, double qty, string itemCode, string whsCode)>();
            for (int i = 0; i < request.Lines.Count; i++)
            {
                order.Lines.SetCurrentLine(i);
                lineCaptures.Add((order.Lines.LineNum, order.Lines.Quantity, order.Lines.ItemCode, order.Lines.WarehouseCode));
            }

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
                    "AutoCreatePickList enabled — creating pick list for DocEntry={DocEntry} with {LineCount} line(s)",
                    docEntry, lineCaptures.Count);

                try
                {
                    var binPriority = _settings.BinLocationPriority;
                    bool useBinAllocation = binPriority != null && binPriority.Count > 0;

                    // ── Resolve bin AbsEntry values and stock levels ──
                    // Maps: BinCode → (AbsEntry, WhsCode, { ItemCode → AvailableQty })
                    var binInfo = new Dictionary<string, (int absEntry, string whsCode, Dictionary<string, double> stock)>();

                    if (useBinAllocation)
                    {
                        // Collect all unique item codes from SO lines
                        var itemCodes = lineCaptures.Select(lc => lc.itemCode).Distinct().ToList();
                        var itemCodesForSql = string.Join(",", itemCodes.Select(ic => $"'{ic.Replace("'", "''")}'"));
                        var binCodesForSql = string.Join(",", binPriority!.Select(bc => $"'{bc.Replace("'", "''")}'"));

                        var rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
                        rs.DoQuery(
                            $"SELECT BIN.\"AbsEntry\", BIN.\"BinCode\", BIN.\"WhsCode\", IB.\"ItemCode\", IB.\"OnHandQty\" " +
                            $"FROM OBIN BIN " +
                            $"INNER JOIN OIBQ IB ON IB.\"BinAbs\" = BIN.\"AbsEntry\" " +
                            $"WHERE BIN.\"BinCode\" IN ({binCodesForSql}) " +
                            $"AND IB.\"ItemCode\" IN ({itemCodesForSql}) " +
                            $"AND IB.\"OnHandQty\" > 0");

                        while (!rs.EoF)
                        {
                            var binCode = (string)rs.Fields.Item("BinCode").Value;
                            var absEntry = (int)rs.Fields.Item("AbsEntry").Value;
                            var whsCode = (string)rs.Fields.Item("WhsCode").Value;
                            var itemCode = (string)rs.Fields.Item("ItemCode").Value;
                            var onHand = Convert.ToDouble(rs.Fields.Item("OnHandQty").Value);

                            if (!binInfo.ContainsKey(binCode))
                                binInfo[binCode] = (absEntry, whsCode, new Dictionary<string, double>());
                            binInfo[binCode].stock[itemCode] = onHand;

                            rs.MoveNext();
                        }
                        Marshal.ReleaseComObject(rs);

                        _logger.LogInformation(
                            "Bin stock query returned data for {BinCount} bins across {ItemCount} items",
                            binInfo.Count, itemCodes.Count);
                    }

                    // ── Build pick list with cascading bin allocation ──
                    var pickList = (PickLists)_company!.GetBusinessObject(BoObjectTypes.oPickLists);
                    pickList.PickDate = DateTime.Now;

                    bool anyLineAllocated = false;
                    int pickLineIndex = 0;

                    for (int i = 0; i < lineCaptures.Count; i++)
                    {
                        var (lineNum, qty, itemCode, lineWhsCode) = lineCaptures[i];

                        if (!useBinAllocation)
                        {
                            // Legacy behavior: no bin allocation
                            if (pickLineIndex > 0) pickList.Lines.Add();
                            pickList.Lines.BaseObjectType = ((int)BoObjectTypes.oOrders).ToString();
                            pickList.Lines.OrderEntry = docEntry;
                            pickList.Lines.OrderRowID = lineNum;
                            pickList.Lines.ReleasedQuantity = qty;
                            anyLineAllocated = true;
                            pickLineIndex++;
                            continue;
                        }

                        // Cascading allocation across priority bins
                        double remaining = qty;
                        var allocations = new List<(int binAbsEntry, string binCode, double allocQty)>();

                        foreach (var binCode in binPriority!)
                        {
                            if (remaining <= 0) break;

                            if (!binInfo.TryGetValue(binCode, out var bin)) continue;
                            // Only use bins that belong to the same warehouse as the SO line
                            if (!string.IsNullOrEmpty(lineWhsCode)
                                && !string.Equals(bin.whsCode, lineWhsCode, StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (!bin.stock.TryGetValue(itemCode, out var available) || available <= 0) continue;

                            double take = Math.Min(remaining, available);
                            allocations.Add((bin.absEntry, binCode, take));
                            remaining -= take;
                            // Reduce available so subsequent lines don't over-allocate
                            bin.stock[itemCode] = available - take;
                        }

                        if (remaining > 0)
                        {
                            // Not enough stock across all bins — skip this line entirely
                            _logger.LogWarning(
                                "Skipping pick list for item {ItemCode} (line {LineNum}): " +
                                "need {Required}, only {Available} available across priority bins",
                                itemCode, lineNum, qty, qty - remaining);
                            continue;
                        }

                        // Add pick list line with bin allocations
                        if (pickLineIndex > 0) pickList.Lines.Add();
                        pickList.Lines.BaseObjectType = ((int)BoObjectTypes.oOrders).ToString();
                        pickList.Lines.OrderEntry = docEntry;
                        pickList.Lines.OrderRowID = lineNum;
                        pickList.Lines.ReleasedQuantity = qty;

                        for (int b = 0; b < allocations.Count; b++)
                        {
                            if (b > 0) pickList.Lines.BinAllocations.Add();
                            pickList.Lines.BinAllocations.BinAbsEntry = allocations[b].binAbsEntry;
                            pickList.Lines.BinAllocations.Quantity = allocations[b].allocQty;
                            pickList.Lines.BinAllocations.SerialAndBatchNumbersBaseLine = pickLineIndex;

                            _logger.LogInformation(
                                "  Item {ItemCode} line {LineNum}: bin {BinCode} (AbsEntry={AbsEntry}) → {Qty}",
                                itemCode, lineNum, allocations[b].binCode,
                                allocations[b].binAbsEntry, allocations[b].allocQty);
                        }

                        anyLineAllocated = true;
                        pickLineIndex++;
                    }

                    if (!anyLineAllocated)
                    {
                        _logger.LogWarning(
                            "No lines could be fully allocated from priority bins — skipping pick list creation for DocEntry={DocEntry}",
                            docEntry);
                        Marshal.ReleaseComObject(pickList);
                    }
                    else
                    {
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
                                "Pick list created: AbsEntry={PickListEntry}", pickListEntry);
                            response.PickListEntry = pickListEntry;
                        }

                        Marshal.ReleaseComObject(pickList);
                    }
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
    }

    public async Task<SapSalesOrderResponse> UpdateSalesOrderAsync(int docEntry, SapSalesOrderRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            _logger.LogInformation(
                "Updating SAP SO — DocEntry={DocEntry}, ResolvedSoId={ResolvedSoId}",
                docEntry, request.ResolvedSoId);

            var order = (Documents)_company!.GetBusinessObject(BoObjectTypes.oOrders);

            if (!order.GetByKey(docEntry))
            {
                Marshal.ReleaseComObject(order);
                throw new InvalidOperationException(
                    $"SAP B1 Sales Order with DocEntry={docEntry} not found.");
            }

            // If document is closed (fully delivered/invoiced), skip update gracefully.
            if (order.DocumentStatus != BoStatus.bost_Open)
            {
                int closedDocNum = order.DocNum;
                Marshal.ReleaseComObject(order);

                _logger.LogInformation(
                    "SAP Sales Order DocEntry={DocEntry} (DocNum={DocNum}) is closed — " +
                    "skipping UDF update. This is expected after delivery/invoicing.",
                    docEntry, closedDocNum);

                return new SapSalesOrderResponse
                {
                    DocEntry = docEntry,
                    DocNum = closedDocNum,
                    UOdooSoId = request.ResolvedSoId
                };
            }

            int docNum = order.DocNum;

            TrySetUserField(order.UserFields, "U_Odoo_SO_ID", request.ResolvedSoId, "SO header update");

            var syncDate = DateTime.UtcNow.Date;
            TrySetUserField(order.UserFields, "U_Odoo_LastSync", syncDate, "SO header update");
            TrySetUserField(order.UserFields, "U_Odoo_SyncDir", SyncDirectionOdooToSap, "SO header update");

            var deliveryId = request.ResolvedDeliveryId;
            if (!string.IsNullOrEmpty(deliveryId))
            {
                TrySetUserField(order.UserFields, "U_Odoo_Delivery_ID", deliveryId, "SO header update");
                _logger.LogDebug("UDF U_Odoo_Delivery_ID set to '{Value}' on SO header update.", deliveryId);
            }

            _logger.LogDebug(
                "UDF U_Odoo_LastSync set to '{Value}' and U_Odoo_SyncDir set to '{SyncDir}' on SO header update.",
                syncDate, SyncDirectionOdooToSap);

            // Update line quantities when lines are provided
            if (request.Lines.Count > 0)
            {
                _UpdateSalesOrderLines(order, request);
            }

            int result = order.Update();

            if (result != 0)
            {
                _company.GetLastError(out int errCode, out string errMsg);
                Marshal.ReleaseComObject(order);
                throw new InvalidOperationException(
                    $"SAP DI API error {errCode}: {errMsg}");
            }

            Marshal.ReleaseComObject(order);

            _logger.LogInformation(
                "✅ SAP SO updated: DocEntry={DocEntry}, DocNum={DocNum}",
                docEntry, docNum);

            // Refresh pick list if lines were updated
            if (request.Lines.Count > 0 && _settings.AutoCreatePickList)
            {
                _RefreshPickListForSO(docEntry, request);
            }

            return new SapSalesOrderResponse
            {
                DocEntry = docEntry,
                DocNum = docNum,
                UOdooSoId = request.ResolvedSoId
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Updates SAP SO line quantities to match the incoming request.
    /// Matches lines by ItemCode. For matched lines, updates Quantity
    /// and UnitPrice. New items are appended as additional lines.
    /// </summary>
    private void _UpdateSalesOrderLines(Documents order, SapSalesOrderRequest request)
    {
        int sapLineCount = order.Lines.Count;

        // Build a map of existing SAP lines by ItemCode
        var sapLines = new Dictionary<string, int>();
        for (int i = 0; i < sapLineCount; i++)
        {
            order.Lines.SetCurrentLine(i);
            var ic = order.Lines.ItemCode ?? "";
            if (!sapLines.ContainsKey(ic))
                sapLines[ic] = i;
        }

        foreach (var reqLine in request.Lines)
        {
            if (sapLines.TryGetValue(reqLine.ItemCode, out int sapIdx))
            {
                // Update existing line
                order.Lines.SetCurrentLine(sapIdx);
                double oldQty = order.Lines.Quantity;
                if (Math.Abs(oldQty - reqLine.Quantity) > 0.001)
                {
                    order.Lines.Quantity = reqLine.Quantity;
                    _logger.LogInformation(
                        "SO line updated: ItemCode={ItemCode}, Qty {OldQty} -> {NewQty}",
                        reqLine.ItemCode, oldQty, reqLine.Quantity);
                }
                if (Math.Abs(order.Lines.UnitPrice - reqLine.UnitPrice) > 0.001)
                {
                    order.Lines.UnitPrice = reqLine.UnitPrice;
                }
            }
            else
            {
                // New line — append
                order.Lines.Add();
                order.Lines.ItemCode = reqLine.ItemCode;
                order.Lines.Quantity = reqLine.Quantity;
                order.Lines.UnitPrice = reqLine.UnitPrice;

                if (!string.IsNullOrEmpty(reqLine.WarehouseCode))
                    order.Lines.WarehouseCode = reqLine.WarehouseCode;

                _logger.LogInformation(
                    "SO line added: ItemCode={ItemCode}, Qty={Qty}, Price={Price}",
                    reqLine.ItemCode, reqLine.Quantity, reqLine.UnitPrice);
            }
        }
    }

    /// <summary>
    /// Closes an existing open pick list for the SO and creates a new
    /// one with current SO line quantities.  If the pick list has
    /// already been picked (status != Released), it is left untouched.
    /// </summary>
    private void _RefreshPickListForSO(int soDocEntry, SapSalesOrderRequest request)
    {
        try
        {
            int? existingPklEntry = LookupPickListForSalesOrder(soDocEntry);
            if (!existingPklEntry.HasValue)
            {
                _logger.LogInformation(
                    "No existing pick list for SO DocEntry={DocEntry} — creating new one.",
                    soDocEntry);
                _CreatePickListForSO(soDocEntry, request);
                return;
            }

            // Check pick list status
            var rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery(
                $"SELECT \"Status\" FROM \"OPKL\" WHERE \"AbsEntry\" = {existingPklEntry.Value}");

            string pklStatus = "N"; // default unknown
            if (!rs.EoF)
                pklStatus = rs.Fields.Item("Status").Value?.ToString() ?? "N";
            Marshal.ReleaseComObject(rs);

            // Status: R = Released (open), P = Picked, C = Closed/Partially Delivered
            if (pklStatus == "R")
            {
                _logger.LogInformation(
                    "Pick list AbsEntry={PklEntry} is still Released — closing and recreating.",
                    existingPklEntry.Value);

                // Close the old pick list via Close() method
                // (Status property is read-only in SAP DI API)
                var pkl = (PickLists)_company.GetBusinessObject(BoObjectTypes.oPickLists);
                if (pkl.GetByKey(existingPklEntry.Value))
                {
                    int closeResult = pkl.Close();
                    if (closeResult != 0)
                    {
                        _company.GetLastError(out int ec, out string em);
                        _logger.LogWarning(
                            "Failed to close pick list AbsEntry={PklEntry}: {Err}",
                            existingPklEntry.Value, em);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Closed old pick list AbsEntry={PklEntry}.",
                            existingPklEntry.Value);
                    }
                }
                Marshal.ReleaseComObject(pkl);

                // Create new pick list with updated quantities
                _CreatePickListForSO(soDocEntry, request);
            }
            else
            {
                _logger.LogInformation(
                    "Pick list AbsEntry={PklEntry} status={Status} (already picked/closed) "
                    + "— leaving it. New pick list will be created for additional qty if needed.",
                    existingPklEntry.Value, pklStatus);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Pick list refresh failed for SO DocEntry={DocEntry}. "
                + "SO lines were updated successfully — pick list may need manual attention.",
                soDocEntry);
        }
    }

    /// <summary>
    /// Creates a new pick list for the given SO using current line data.
    /// </summary>
    private void _CreatePickListForSO(int soDocEntry, SapSalesOrderRequest request)
    {
        try
        {
            // Re-read the SO to get current line numbers and quantities
            var order = (Documents)_company!.GetBusinessObject(BoObjectTypes.oOrders);
            if (!order.GetByKey(soDocEntry))
            {
                Marshal.ReleaseComObject(order);
                return;
            }

            int lineCount = order.Lines.Count;
            var pickList = (PickLists)_company.GetBusinessObject(BoObjectTypes.oPickLists);
            pickList.PickDate = DateTime.Now;

            for (int i = 0; i < lineCount; i++)
            {
                order.Lines.SetCurrentLine(i);

                // Skip fully delivered lines (open qty = 0)
                double openQty = order.Lines.RemainingOpenQuantity;
                if (openQty <= 0)
                    continue;

                if (i > 0) pickList.Lines.Add();
                pickList.Lines.BaseObjectType = ((int)BoObjectTypes.oOrders).ToString();
                pickList.Lines.OrderEntry = soDocEntry;
                pickList.Lines.OrderRowID = order.Lines.LineNum;
                pickList.Lines.ReleasedQuantity = openQty;
            }

            Marshal.ReleaseComObject(order);

            int plResult = pickList.Add();
            if (plResult != 0)
            {
                _company.GetLastError(out int ec, out string em);
                _logger.LogWarning(
                    "New pick list creation failed for SO DocEntry={DocEntry}: {Err}",
                    soDocEntry, em);
            }
            else
            {
                int newPklEntry = int.Parse(_company.GetNewObjectKey());
                _logger.LogInformation(
                    "New pick list created: AbsEntry={PklEntry} for SO DocEntry={DocEntry}",
                    newPklEntry, soDocEntry);
            }
            Marshal.ReleaseComObject(pickList);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to create new pick list for SO DocEntry={DocEntry}.",
                soDocEntry);
        }
    }

    // ================================
    // AR INVOICE (copy from Delivery)
    // ================================
    public async Task<SapInvoiceResponse> CreateInvoiceAsync(SapInvoiceRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            _logger.LogInformation(
                "Received AR Invoice creation request — ExternalInvoiceId={ExternalInvoiceId}, CustomerCode={CustomerCode}, " +
                "CopyFromDelivery={CopyFromDelivery}, SapDeliveryDocEntry={SapDeliveryDocEntry}, SapSalesOrderDocEntry={SapSalesOrderDocEntry}, " +
                "LineCount={LineCount}",
                request.ExternalInvoiceId,
                request.CustomerCode,
                request.CopyFromDelivery,
                request.SapDeliveryDocEntry,
                request.SapSalesOrderDocEntry,
                request.Lines.Count);

            var invoice = (Documents)_company!.GetBusinessObject(BoObjectTypes.oInvoices);

            if (request.CopyFromDelivery)
            {
                try
                {
                    return CreateInvoiceCopyFromDelivery(invoice, request);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("-5002"))
                {
                    // -5002 = "One of the base documents has already been closed"
                    // The delivery was manually closed in SAP B1.  Fall back to
                    // the manual invoice path, but clear the delivery base
                    // references on each line (they would also trigger -5002)
                    // and use the Sales Order as the base document instead so
                    // the relationship map still shows SO → Invoice.
                    _logger.LogWarning(
                        "Copy-From-Delivery failed with -5002 (base document closed) " +
                        "for Delivery DocEntry={DeliveryDocEntry}. " +
                        "Falling back to manual invoice creation based on Sales Order.",
                        request.SapDeliveryDocEntry);

                    // Strip delivery base references from every line so the
                    // manual path won't try to link back to the closed delivery.
                    foreach (var line in request.Lines)
                    {
                        line.BaseDeliveryDocEntry = null;
                        line.BaseDeliveryLineNum = null;
                    }

                    // Need a fresh Documents object — the failed one may be dirty.
                    Marshal.ReleaseComObject(invoice);
                    invoice = (Documents)_company!.GetBusinessObject(BoObjectTypes.oInvoices);

                    try
                    {
                        return CreateInvoiceManual(invoice, request);
                    }
                    catch (InvalidOperationException ex2) when (ex2.Message.Contains("-5002"))
                    {
                        // The Sales Order is also closed.  Last resort: create a
                        // standalone invoice with no base document references at all.
                        _logger.LogWarning(
                            "SO-based fallback also failed with -5002 (SO DocEntry={SoDocEntry} is closed). " +
                            "Creating standalone invoice with no base document references.",
                            request.SapSalesOrderDocEntry);

                        // Clear the SO reference so the manual path skips base linking entirely.
                        request.SapSalesOrderDocEntry = null;

                        Marshal.ReleaseComObject(invoice);
                        invoice = (Documents)_company!.GetBusinessObject(BoObjectTypes.oInvoices);
                        return CreateInvoiceManual(invoice, request);
                    }
                }
            }
            else
            {
                return CreateInvoiceManual(invoice, request);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Creates an AR Invoice by copying from a Delivery Note (ODLN).
    /// This preserves the full document chain: Sales Order → Delivery → Invoice.
    /// SAP B1 uses BaseType / BaseEntry / BaseLine on each invoice line to link back
    /// to the originating delivery line.
    /// </summary>
    private SapInvoiceResponse CreateInvoiceCopyFromDelivery(Documents invoice, SapInvoiceRequest request)
    {
        int deliveryDocEntry = request.SapDeliveryDocEntry!.Value;

        _logger.LogInformation(
            "Creating AR Invoice by copying from Delivery DocEntry={DeliveryDocEntry}",
            deliveryDocEntry);

        // Load the source delivery to read its lines
        var delivery = (Documents)_company!.GetBusinessObject(BoObjectTypes.oDeliveryNotes);

        if (!delivery.GetByKey(deliveryDocEntry))
        {
            Marshal.ReleaseComObject(delivery);
            Marshal.ReleaseComObject(invoice);
            throw new InvalidOperationException(
                $"SAP B1 Delivery Note with DocEntry={deliveryDocEntry} not found. " +
                "Cannot create invoice by copy-from-delivery.");
        }

        _logger.LogInformation(
            "Loaded source Delivery: DocEntry={DocEntry}, DocNum={DocNum}, CardCode={CardCode}, LineCount={LineCount}",
            delivery.DocEntry,
            delivery.DocNum,
            delivery.CardCode,
            delivery.Lines.Count);

        // Set invoice header fields
        invoice.CardCode = !string.IsNullOrEmpty(request.CustomerCode)
            ? request.CustomerCode
            : delivery.CardCode;

        invoice.NumAtCard = request.ExternalInvoiceId;

        if (request.SlpCode.HasValue && request.SlpCode.Value >= 0)
            invoice.SalesPersonCode = request.SlpCode.Value;

        if (request.DocDate.HasValue)
            invoice.DocDate = request.DocDate.Value;

        if (request.DueDate.HasValue)
            invoice.DocDueDate = request.DueDate.Value;

        if (!string.IsNullOrEmpty(request.Currency))
            invoice.DocCurrency = request.Currency;

        // Set header UDFs for Odoo traceability
        TrySetUserField(invoice.UserFields, "U_Odoo_Invoice_ID", request.ExternalInvoiceId, "Invoice header");

        if (!string.IsNullOrEmpty(request.UOdooSoId))
            TrySetUserField(invoice.UserFields, "U_Odoo_SO_ID", request.UOdooSoId, "Invoice header");

        var syncDate = DateTime.UtcNow.Date;
        TrySetUserField(invoice.UserFields, "U_Odoo_LastSync", syncDate, "Invoice header");
        TrySetUserField(invoice.UserFields, "U_Odoo_SyncDir", SyncDirectionOdooToSap, "Invoice header");

        // Copy lines from the delivery document.
        // Each invoice line references its source delivery line via BaseType/BaseEntry/BaseLine.
        // We set line fields explicitly (ItemCode, Quantity, Price, WarehouseCode) instead
        // of relying on SAP's automatic copy, because auto-copy also inherits bin
        // allocations from the delivery.  When the pick list allocated stock from a
        // bin in a different warehouse (e.g. COCWHSE bin on a MainWHSE line), the
        // inherited bin/warehouse mismatch causes DI API error -10.  Invoices don't
        // move stock so bin allocations are unnecessary.
        int deliveryLineCount = delivery.Lines.Count;

        _logger.LogInformation(
            "Copying {DeliveryLineCount} line(s) from Delivery DocEntry={DeliveryDocEntry} to Invoice (explicit lines, no bin allocations)",
            deliveryLineCount, deliveryDocEntry);

        for (int i = 0; i < deliveryLineCount; i++)
        {
            delivery.Lines.SetCurrentLine(i);

            if (i > 0)
                invoice.Lines.Add();

            // Set line data explicitly from the delivery
            invoice.Lines.ItemCode = delivery.Lines.ItemCode;
            invoice.Lines.Quantity = delivery.Lines.Quantity;
            invoice.Lines.UnitPrice = delivery.Lines.UnitPrice;
            invoice.Lines.WarehouseCode = delivery.Lines.WarehouseCode;

            // Set base document reference to maintain the document chain
            invoice.Lines.BaseType = (int)BoObjectTypes.oDeliveryNotes;
            invoice.Lines.BaseEntry = deliveryDocEntry;
            invoice.Lines.BaseLine = delivery.Lines.LineNum;

            _logger.LogDebug(
                "Invoice Line[{Index}]: BaseType=oDeliveryNotes, BaseEntry={BaseEntry}, BaseLine={BaseLine}, " +
                "ItemCode={ItemCode}, Qty={Qty}, Price={Price}, Warehouse={Warehouse}",
                i,
                deliveryDocEntry,
                delivery.Lines.LineNum,
                delivery.Lines.ItemCode,
                delivery.Lines.Quantity,
                delivery.Lines.UnitPrice,
                delivery.Lines.WarehouseCode);
        }

        // Capture the originating Sales Order DocEntry from the delivery for the response
        int? baseSoDocEntry = request.SapSalesOrderDocEntry;

        Marshal.ReleaseComObject(delivery);

        // Add the invoice
        int result = invoice.Add();

        if (result != 0)
        {
            _company!.GetLastError(out int errCode, out string errMsg);
            Marshal.ReleaseComObject(invoice);

            _logger.LogError(
                "Failed to create AR Invoice from Delivery DocEntry={DeliveryDocEntry}: DI API error {ErrCode}: {ErrMsg}",
                deliveryDocEntry, errCode, errMsg);

            throw new InvalidOperationException(
                $"SAP DI API error {errCode}: {errMsg}");
        }

        int invoiceDocEntry = int.Parse(_company!.GetNewObjectKey());

        // Retrieve the created invoice to get DocNum and line-level data
        invoice.GetByKey(invoiceDocEntry);
        int invoiceDocNum = invoice.DocNum;

        var lineResponses = ReadInvoiceLines(invoice);

        Marshal.ReleaseComObject(invoice);

        _logger.LogInformation(
            "AR Invoice created successfully (copy-from-delivery): DocEntry={DocEntry}, DocNum={DocNum}, " +
            "ExternalInvoiceId={ExternalInvoiceId}, BaseDeliveryDocEntry={BaseDeliveryDocEntry}, " +
            "BaseSalesOrderDocEntry={BaseSalesOrderDocEntry}, LineCount={LineCount}",
            invoiceDocEntry,
            invoiceDocNum,
            request.ExternalInvoiceId,
            deliveryDocEntry,
            baseSoDocEntry,
            lineResponses.Count);

        return new SapInvoiceResponse
        {
            DocEntry = invoiceDocEntry,
            DocNum = invoiceDocNum,
            ExternalInvoiceId = request.ExternalInvoiceId,
            BaseDeliveryDocEntry = deliveryDocEntry,
            BaseSalesOrderDocEntry = baseSoDocEntry,
            Lines = lineResponses
        };
    }

    /// <summary>
    /// Creates an AR Invoice manually from the provided line items
    /// (fallback when no delivery DocEntry is supplied).
    /// </summary>
    private SapInvoiceResponse CreateInvoiceManual(Documents invoice, SapInvoiceRequest request)
    {
        _logger.LogInformation(
            "Creating AR Invoice manually (no copy-from-delivery) — ExternalInvoiceId={ExternalInvoiceId}, LineCount={LineCount}",
            request.ExternalInvoiceId,
            request.Lines.Count);

        if (request.Lines.Count == 0)
        {
            Marshal.ReleaseComObject(invoice);
            throw new InvalidOperationException(
                "Invoice lines are required when SapDeliveryDocEntry is not provided.");
        }

        // Set header
        invoice.CardCode = request.CustomerCode;
        invoice.NumAtCard = request.ExternalInvoiceId;

        if (request.SlpCode.HasValue && request.SlpCode.Value >= 0)
            invoice.SalesPersonCode = request.SlpCode.Value;

        if (request.DocDate.HasValue)
            invoice.DocDate = request.DocDate.Value;

        if (request.DueDate.HasValue)
            invoice.DocDueDate = request.DueDate.Value;

        if (!string.IsNullOrEmpty(request.Currency))
            invoice.DocCurrency = request.Currency;

        // Header UDFs
        TrySetUserField(invoice.UserFields, "U_Odoo_Invoice_ID", request.ExternalInvoiceId, "Invoice header");

        if (!string.IsNullOrEmpty(request.UOdooSoId))
            TrySetUserField(invoice.UserFields, "U_Odoo_SO_ID", request.UOdooSoId, "Invoice header");

        var syncDate = DateTime.UtcNow.Date;
        TrySetUserField(invoice.UserFields, "U_Odoo_LastSync", syncDate, "Invoice header");
        TrySetUserField(invoice.UserFields, "U_Odoo_SyncDir", SyncDirectionOdooToSap, "Invoice header");

        // Set lines
        // ── Pre-query bin stock if bin allocation is configured ──
        var binPriority = _settings.BinLocationPriority;
        bool useBinAllocation = binPriority != null && binPriority.Count > 0;
        var binInfo = new Dictionary<string, (int absEntry, string whsCode, Dictionary<string, double> stock)>();

        if (useBinAllocation)
        {
            var itemCodes = request.Lines.Select(l => l.ItemCode).Distinct().ToList();
            var itemCodesForSql = string.Join(",", itemCodes.Select(ic => $"'{ic.Replace("'", "''")}'"));
            var binCodesForSql = string.Join(",", binPriority!.Select(bc => $"'{bc.Replace("'", "''")}'"));

            var rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery(
                $"SELECT BIN.\"AbsEntry\", BIN.\"BinCode\", BIN.\"WhsCode\", IB.\"ItemCode\", IB.\"OnHandQty\" " +
                $"FROM OBIN BIN " +
                $"INNER JOIN OIBQ IB ON IB.\"BinAbs\" = BIN.\"AbsEntry\" " +
                $"WHERE BIN.\"BinCode\" IN ({binCodesForSql}) " +
                $"AND IB.\"ItemCode\" IN ({itemCodesForSql}) " +
                $"AND IB.\"OnHandQty\" > 0");

            while (!rs.EoF)
            {
                var binCode = (string)rs.Fields.Item("BinCode").Value;
                var absEntry = (int)rs.Fields.Item("AbsEntry").Value;
                var whsCode = (string)rs.Fields.Item("WhsCode").Value;
                var itemCode = (string)rs.Fields.Item("ItemCode").Value;
                var onHand = Convert.ToDouble(rs.Fields.Item("OnHandQty").Value);

                if (!binInfo.ContainsKey(binCode))
                    binInfo[binCode] = (absEntry, whsCode, new Dictionary<string, double>());
                binInfo[binCode].stock[itemCode] = onHand;

                rs.MoveNext();
            }
            Marshal.ReleaseComObject(rs);

            _logger.LogInformation(
                "Invoice bin stock query: {BinCount} bins with stock for {ItemCount} items",
                binInfo.Count, itemCodes.Count);
        }

        // ── Pre-load SO line numbers for SO-based linking ──
        // When delivery references are absent but a Sales Order DocEntry
        // is available, we map each invoice line to the corresponding SO
        // line by ItemCode so the relationship map shows SO → Invoice.
        Dictionary<string, List<int>>? soLineMap = null;
        bool needSoFallback = request.SapSalesOrderDocEntry.HasValue
            && request.SapSalesOrderDocEntry.Value > 0
            && request.Lines.Any(l => !l.BaseDeliveryDocEntry.HasValue);

        if (needSoFallback)
        {
            soLineMap = new Dictionary<string, List<int>>();
            var soDoc = (Documents)_company!.GetBusinessObject(BoObjectTypes.oOrders);
            if (soDoc.GetByKey(request.SapSalesOrderDocEntry.Value))
            {
                for (int s = 0; s < soDoc.Lines.Count; s++)
                {
                    soDoc.Lines.SetCurrentLine(s);
                    string itemCode = soDoc.Lines.ItemCode;
                    if (!soLineMap.ContainsKey(itemCode))
                        soLineMap[itemCode] = new List<int>();
                    soLineMap[itemCode].Add(soDoc.Lines.LineNum);
                }
                _logger.LogInformation(
                    "Loaded SO DocEntry={SoDocEntry} for base-document fallback: {LineCount} lines",
                    request.SapSalesOrderDocEntry.Value, soDoc.Lines.Count);
            }
            else
            {
                _logger.LogWarning(
                    "Could not load SO DocEntry={SoDocEntry} for base-document fallback",
                    request.SapSalesOrderDocEntry.Value);
            }
            Marshal.ReleaseComObject(soDoc);
        }

        for (int i = 0; i < request.Lines.Count; i++)
        {
            if (i > 0)
                invoice.Lines.Add();

            var line = request.Lines[i];

            invoice.Lines.ItemCode = line.ItemCode;
            invoice.Lines.Quantity = line.Quantity;
            invoice.Lines.UnitPrice = line.Price;

            if (line.DiscountPercent.HasValue)
                invoice.Lines.DiscountPercent = line.DiscountPercent.Value;

            if (!string.IsNullOrEmpty(line.WarehouseCode))
                invoice.Lines.WarehouseCode = line.WarehouseCode;

            if (!string.IsNullOrEmpty(line.AccountCode))
                invoice.Lines.AccountCode = line.AccountCode;

            // Link to base document — prefer Delivery, fall back to Sales Order.
            if (line.BaseDeliveryDocEntry.HasValue && line.BaseDeliveryLineNum.HasValue)
            {
                invoice.Lines.BaseType = (int)BoObjectTypes.oDeliveryNotes;
                invoice.Lines.BaseEntry = line.BaseDeliveryDocEntry.Value;
                invoice.Lines.BaseLine = line.BaseDeliveryLineNum.Value;

                _logger.LogDebug(
                    "Manual Invoice Line[{Index}]: BaseType=Delivery, BaseEntry={BaseEntry}, BaseLine={BaseLine}",
                    i, line.BaseDeliveryDocEntry.Value, line.BaseDeliveryLineNum.Value);
            }
            else if (request.SapSalesOrderDocEntry.HasValue && request.SapSalesOrderDocEntry.Value > 0)
            {
                // Delivery reference unavailable (e.g. delivery was closed).
                // Link to the Sales Order instead so the relationship map
                // shows SO → Invoice.  Match by ItemCode + line index.
                if (soLineMap != null && soLineMap.TryGetValue(line.ItemCode, out var soLineNums) && soLineNums.Count > 0)
                {
                    int soLine = soLineNums[0];
                    soLineNums.RemoveAt(0);
                    invoice.Lines.BaseType = (int)BoObjectTypes.oOrders;
                    invoice.Lines.BaseEntry = request.SapSalesOrderDocEntry.Value;
                    invoice.Lines.BaseLine = soLine;

                    _logger.LogDebug(
                        "Manual Invoice Line[{Index}]: BaseType=SalesOrder, BaseEntry={BaseEntry}, BaseLine={BaseLine}",
                        i, request.SapSalesOrderDocEntry.Value, soLine);
                }
            }

            // ── Cascading bin allocation for this line ──
            if (useBinAllocation && !line.BaseDeliveryDocEntry.HasValue)
            {
                // Resolve the warehouse for this line — explicit, default, or SAP fallback
                string lineWhsCode = !string.IsNullOrEmpty(line.WarehouseCode)
                    ? line.WarehouseCode
                    : _settings.DefaultWarehouseCode ?? "";

                double remaining = line.Quantity;
                var allocations = new List<(int binAbsEntry, string binCode, double allocQty)>();

                foreach (var binCode in binPriority!)
                {
                    if (remaining <= 0) break;
                    if (!binInfo.TryGetValue(binCode, out var bin)) continue;
                    // Only use bins that belong to the same warehouse as the line
                    if (!string.IsNullOrEmpty(lineWhsCode)
                        && !string.Equals(bin.whsCode, lineWhsCode, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!bin.stock.TryGetValue(line.ItemCode, out var available) || available <= 0) continue;

                    double take = Math.Min(remaining, available);
                    allocations.Add((bin.absEntry, binCode, take));
                    remaining -= take;
                    bin.stock[line.ItemCode] = available - take;
                }

                if (remaining <= 0 && allocations.Count > 0)
                {
                    for (int b = 0; b < allocations.Count; b++)
                    {
                        if (b > 0) invoice.Lines.BinAllocations.Add();
                        invoice.Lines.BinAllocations.BinAbsEntry = allocations[b].binAbsEntry;
                        invoice.Lines.BinAllocations.Quantity = allocations[b].allocQty;
                        invoice.Lines.BinAllocations.SerialAndBatchNumbersBaseLine = i;

                        _logger.LogInformation(
                            "  Invoice line[{Index}] item {ItemCode}: bin {BinCode} (AbsEntry={AbsEntry}) → {Qty}",
                            i, line.ItemCode, allocations[b].binCode,
                            allocations[b].binAbsEntry, allocations[b].allocQty);
                    }
                }
                else if (remaining > 0)
                {
                    _logger.LogWarning(
                        "Invoice line[{Index}] item {ItemCode}: insufficient bin stock " +
                        "(need {Required}, have {Available}) — letting SAP resolve",
                        i, line.ItemCode, line.Quantity, line.Quantity - remaining);
                }
            }

            _logger.LogDebug(
                "Manual Invoice Line[{Index}]: ItemCode={ItemCode}, Qty={Qty}, Price={Price}, Discount={Discount}",
                i, line.ItemCode, line.Quantity, line.Price, line.DiscountPercent);
        }

        int result = invoice.Add();

        if (result != 0)
        {
            _company!.GetLastError(out int errCode, out string errMsg);
            Marshal.ReleaseComObject(invoice);

            _logger.LogError(
                "Failed to create AR Invoice manually: DI API error {ErrCode}: {ErrMsg}",
                errCode, errMsg);

            throw new InvalidOperationException(
                $"SAP DI API error {errCode}: {errMsg}");
        }

        int invoiceDocEntry = int.Parse(_company!.GetNewObjectKey());

        invoice.GetByKey(invoiceDocEntry);
        int invoiceDocNum = invoice.DocNum;

        var lineResponses = ReadInvoiceLines(invoice);

        Marshal.ReleaseComObject(invoice);

        _logger.LogInformation(
            "AR Invoice created successfully (manual): DocEntry={DocEntry}, DocNum={DocNum}, " +
            "ExternalInvoiceId={ExternalInvoiceId}, LineCount={LineCount}",
            invoiceDocEntry,
            invoiceDocNum,
            request.ExternalInvoiceId,
            lineResponses.Count);

        return new SapInvoiceResponse
        {
            DocEntry = invoiceDocEntry,
            DocNum = invoiceDocNum,
            ExternalInvoiceId = request.ExternalInvoiceId,
            BaseSalesOrderDocEntry = request.SapSalesOrderDocEntry,
            Lines = lineResponses
        };
    }

    /// <summary>
    /// Reads all lines from a loaded SAP Invoice (OINV → INV1) and returns
    /// line-level data including LineNum and GrossBuyPrice for COGS tracking.
    /// Must be called while the Documents COM object is still loaded (before ReleaseComObject).
    /// </summary>
    private List<SapInvoiceLineResponse> ReadInvoiceLines(Documents invoice)
    {
        var lines = new List<SapInvoiceLineResponse>();
        int lineCount = invoice.Lines.Count;

        for (int i = 0; i < lineCount; i++)
        {
            invoice.Lines.SetCurrentLine(i);

            var lineResponse = new SapInvoiceLineResponse
            {
                LineNum = invoice.Lines.LineNum,
                ItemCode = invoice.Lines.ItemCode,
                Quantity = invoice.Lines.Quantity,
                GrossBuyPrice = invoice.Lines.GrossBuyPrice
            };

            lines.Add(lineResponse);

            _logger.LogDebug(
                "Invoice Line[{Index}]: LineNum={LineNum}, ItemCode={ItemCode}, Qty={Qty}, GrossBuyPrice={GrossBuyPrice}",
                i, lineResponse.LineNum, lineResponse.ItemCode, lineResponse.Quantity, lineResponse.GrossBuyPrice);
        }

        return lines;
    }

    // ================================
    // INCOMING PAYMENT (ORCT)
    // ================================
    public async Task<SapIncomingPaymentResponse> CreateIncomingPaymentAsync(SapIncomingPaymentRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();
            return CreateIncomingPaymentCore(request);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Core creation logic for an Incoming Payment (ORCT).
    /// Caller must hold <see cref="_lock"/> and call <see cref="EnsureConnected"/> first.
    /// </summary>
    private SapIncomingPaymentResponse CreateIncomingPaymentCore(SapIncomingPaymentRequest request)
    {
        _logger.LogInformation(
            "Received Incoming Payment creation request — ExternalPaymentId={ExternalPaymentId}, " +
            "CustomerCode={CustomerCode}, DocDate={DocDate}, Currency={Currency}, " +
            "PaymentTotal={PaymentTotal}, IsPartial={IsPartial}, JournalCode={JournalCode}, " +
            "BankOrCashAccountCode={BankOrCashAccountCode}, IsCashPayment={IsCashPayment}, " +
            "OdooPaymentId={OdooPaymentId}, LineCount={LineCount}",
            request.ExternalPaymentId,
            request.CustomerCode,
            request.DocDate,
            request.Currency,
            request.PaymentTotal,
            request.IsPartial,
            request.JournalCode,
            request.BankOrCashAccountCode,
            request.IsCashPayment,
            request.OdooPaymentId,
            request.Lines.Count);

        var payment = (Payments)_company!.GetBusinessObject(BoObjectTypes.oIncomingPayments);

        // Header fields
        payment.CardCode = request.CustomerCode;
        payment.CounterReference = request.ExternalPaymentId;

        if (request.DocDate.HasValue)
            payment.DocDate = request.DocDate.Value;

        if (!string.IsNullOrEmpty(request.Currency))
            payment.DocCurrency = request.Currency;

        if (!string.IsNullOrEmpty(request.JournalRemarks))
            payment.JournalRemarks = request.JournalRemarks;

        // UDF fields — store Odoo identifiers on the SAP payment for traceability
        TrySetUserField(payment.UserFields, "U_Odoo_Payment_ID", request.ExternalPaymentId, "Payment header");

        // Document trail: link payment back to the originating SO and invoice
        if (!string.IsNullOrEmpty(request.UOdooSoId))
            TrySetUserField(payment.UserFields, "U_Odoo_SO_ID", request.UOdooSoId, "Payment header");

        if (!string.IsNullOrEmpty(request.ExternalInvoiceId))
            TrySetUserField(payment.UserFields, "U_Odoo_Invoice_ID", request.ExternalInvoiceId, "Payment header");

        var syncDate = DateTime.UtcNow.Date;
        TrySetUserField(payment.UserFields, "U_Odoo_LastSync", syncDate, "Payment header");
        TrySetUserField(payment.UserFields, "U_Odoo_SyncDir", SyncDirectionOdooToSap, "Payment header");

        _logger.LogDebug(
            "UDFs set on Payment header: U_Odoo_Payment_ID='{PaymentId}', U_Odoo_SO_ID='{SoId}', " +
            "U_Odoo_Invoice_ID='{InvId}', U_Odoo_LastSync={SyncDate}, U_Odoo_SyncDir={SyncDir}",
            request.ExternalPaymentId, request.UOdooSoId, request.ExternalInvoiceId,
            syncDate, SyncDirectionOdooToSap);

        // SAP calculates DocTotal from CashSum / TransferSum / CheckSum,
        // so we only set the appropriate payment-method amount below.

        // Cash vs bank/transfer account
        if (request.IsCashPayment)
        {
            if (!string.IsNullOrEmpty(request.BankOrCashAccountCode))
                payment.CashAccount = request.BankOrCashAccountCode;

            payment.CashSum = request.PaymentTotal;

            _logger.LogInformation(
                "Cash payment — DocTotal={DocTotal}, CashAccount={CashAccount}, CashSum={CashSum}",
                request.PaymentTotal,
                request.BankOrCashAccountCode,
                request.PaymentTotal);
        }
        else
        {
            // Bank / mobile money transfer
            // When a Forex transfer account is specified, use it as the primary transfer account
            // (cross-currency payments route through account 1026216).
            string transferAccount = !string.IsNullOrEmpty(request.ForexAccountCode)
                ? request.ForexAccountCode
                : request.BankOrCashAccountCode ?? string.Empty;

            if (!string.IsNullOrEmpty(transferAccount))
                payment.TransferAccount = transferAccount;

            payment.TransferSum = request.PaymentTotal;

            if (request.DocDate.HasValue)
                payment.TransferDate = request.DocDate.Value;

            _logger.LogInformation(
                "Bank/transfer payment — DocTotal={DocTotal}, TransferAccount={TransferAccount}, TransferSum={TransferSum}, " +
                "ForexAccountCode={ForexAccountCode}",
                request.PaymentTotal,
                transferAccount,
                request.PaymentTotal,
                request.ForexAccountCode);
        }

        // Invoice allocations (RCT2)
        for (int i = 0; i < request.Lines.Count; i++)
        {
            if (i > 0)
                payment.Invoices.Add();

            var line = request.Lines[i];

            payment.Invoices.DocEntry = line.SapInvoiceDocEntry;
            payment.Invoices.InvoiceType = BoRcptInvTypes.it_Invoice;
            payment.Invoices.SumApplied = line.AppliedAmount;

            if (line.DiscountAmount.HasValue && line.DiscountAmount.Value != 0)
            {
                double totalDue = line.AppliedAmount + line.DiscountAmount.Value;
                if (totalDue > 0)
                    payment.Invoices.DiscountPercent = (line.DiscountAmount.Value / totalDue) * 100;
            }

            _logger.LogDebug(
                "Payment allocation Line[{Index}]: SapInvoiceDocEntry={DocEntry}, " +
                "AppliedAmount={AppliedAmount}, DiscountAmount={DiscountAmount}, OdooInvoiceId={OdooInvoiceId}",
                i,
                line.SapInvoiceDocEntry,
                line.AppliedAmount,
                line.DiscountAmount,
                line.OdooInvoiceId);
        }

        int result = payment.Add();

        if (result != 0)
        {
            _company!.GetLastError(out int errCode, out string errMsg);
            Marshal.ReleaseComObject(payment);

            _logger.LogError(
                "Failed to create Incoming Payment for ExternalPaymentId={ExternalPaymentId}: " +
                "DI API error {ErrCode}: {ErrMsg}",
                request.ExternalPaymentId, errCode, errMsg);

            throw new InvalidOperationException(
                $"SAP DI API error {errCode}: {errMsg}");
        }

        int docEntry = int.Parse(_company!.GetNewObjectKey());

        // Retrieve created payment to get DocNum
        payment.GetByKey(docEntry);
        int docNum = payment.DocNum;

        Marshal.ReleaseComObject(payment);

        _logger.LogInformation(
            "Incoming Payment created successfully — DocEntry={DocEntry}, DocNum={DocNum}, " +
            "ExternalPaymentId={ExternalPaymentId}, CustomerCode={CustomerCode}, " +
            "PaymentTotal={PaymentTotal}, LineCount={LineCount}",
            docEntry,
            docNum,
            request.ExternalPaymentId,
            request.CustomerCode,
            request.PaymentTotal,
            request.Lines.Count);

        // Stamp U_Odoo_Payment_ID on each linked SAP invoice so the
        // invoice form shows which Odoo payment settled it.
        StampPaymentIdOnInvoices(request.Lines, request.ExternalPaymentId);

        return new SapIncomingPaymentResponse
        {
            DocEntry = docEntry,
            DocNum = docNum,
            ExternalPaymentId = request.ExternalPaymentId,
            OdooPaymentId = request.OdooPaymentId,
            TotalApplied = request.Lines.Sum(l => l.AppliedAmount)
        };
    }

    /// <summary>
    /// After an Incoming Payment is created, update the <c>U_Odoo_Payment_ID</c>
    /// UDF on every AR Invoice that the payment was allocated against.
    /// This lets SAP users see which Odoo payment settled each invoice.
    /// Failures are logged but do not fail the payment creation.
    /// Caller must already hold <see cref="_lock"/>.
    /// </summary>
    private void StampPaymentIdOnInvoices(
        List<SapIncomingPaymentLineRequest> lines, string paymentId)
    {
        if (string.IsNullOrEmpty(paymentId) || lines.Count == 0)
            return;

        var invoiceDocEntries = lines
            .Select(l => l.SapInvoiceDocEntry)
            .Where(d => d > 0)
            .Distinct()
            .ToList();

        foreach (var invDocEntry in invoiceDocEntries)
        {
            var invoice = (Documents)_company!.GetBusinessObject(BoObjectTypes.oInvoices);
            try
            {
                if (!invoice.GetByKey(invDocEntry))
                {
                    _logger.LogWarning(
                        "Cannot stamp U_Odoo_Payment_ID on invoice DocEntry={DocEntry} — not found",
                        invDocEntry);
                    continue;
                }

                TrySetUserField(invoice.UserFields, "U_Odoo_Payment_ID", paymentId,
                    $"Invoice {invDocEntry} payment stamp");

                int result = invoice.Update();
                if (result != 0)
                {
                    _company!.GetLastError(out int errCode, out string errMsg);
                    _logger.LogWarning(
                        "Failed to stamp U_Odoo_Payment_ID on invoice DocEntry={DocEntry}: {ErrCode} {ErrMsg}",
                        invDocEntry, errCode, errMsg);
                }
                else
                {
                    _logger.LogInformation(
                        "Stamped U_Odoo_Payment_ID='{PaymentId}' on invoice DocEntry={DocEntry}",
                        paymentId, invDocEntry);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Error stamping U_Odoo_Payment_ID on invoice DocEntry={DocEntry}", invDocEntry);
            }
            finally
            {
                Marshal.ReleaseComObject(invoice);
            }
        }
    }

    // ------------------------------------------------------------------
    // Update (re-sync) methods — refresh UDFs on existing SAP documents
    // ------------------------------------------------------------------

    public async Task<SapInvoiceResponse> UpdateInvoiceAsync(int docEntry, SapInvoiceRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            _logger.LogInformation(
                "Updating SAP AR Invoice — DocEntry={DocEntry}, ExternalInvoiceId={ExternalInvoiceId}",
                docEntry, request.ExternalInvoiceId);

            var invoice = (Documents)_company!.GetBusinessObject(BoObjectTypes.oInvoices);

            if (!invoice.GetByKey(docEntry))
            {
                Marshal.ReleaseComObject(invoice);
                throw new InvalidOperationException(
                    $"SAP B1 AR Invoice with DocEntry={docEntry} not found.");
            }

            // Guard: if document is closed (fully paid), skip the update gracefully.
            // This is expected — once a payment is applied, SAP closes the invoice.
            // The update automated action in Odoo may still fire on field changes,
            // but there's nothing to update on a closed document.
            if (invoice.DocumentStatus != BoStatus.bost_Open)
            {
                int closedDocNum = invoice.DocNum;
                var closedLines = ReadInvoiceLines(invoice);
                Marshal.ReleaseComObject(invoice);

                _logger.LogInformation(
                    "SAP AR Invoice DocEntry={DocEntry} (DocNum={DocNum}) is closed (fully paid) — " +
                    "skipping UDF update. This is expected after payment.",
                    docEntry, closedDocNum);

                return new SapInvoiceResponse
                {
                    DocEntry = docEntry,
                    DocNum = closedDocNum,
                    ExternalInvoiceId = request.ExternalInvoiceId,
                    Lines = closedLines
                };
            }

            int docNum = invoice.DocNum;

            // Refresh UDFs
            TrySetUserField(invoice.UserFields, "U_Odoo_Invoice_ID", request.ExternalInvoiceId, "Invoice update");

            if (!string.IsNullOrEmpty(request.UOdooSoId))
                TrySetUserField(invoice.UserFields, "U_Odoo_SO_ID", request.UOdooSoId, "Invoice update");

            var syncDate = DateTime.UtcNow.Date;
            TrySetUserField(invoice.UserFields, "U_Odoo_LastSync", syncDate, "Invoice update");
            TrySetUserField(invoice.UserFields, "U_Odoo_SyncDir", SyncDirectionOdooToSap, "Invoice update");

            int result = invoice.Update();

            if (result != 0)
            {
                _company.GetLastError(out int errCode, out string errMsg);
                Marshal.ReleaseComObject(invoice);
                throw new InvalidOperationException(
                    $"SAP DI API error {errCode}: {errMsg}");
            }

            var lineResponses = ReadInvoiceLines(invoice);
            Marshal.ReleaseComObject(invoice);

            _logger.LogInformation(
                "✅ SAP AR Invoice updated: DocEntry={DocEntry}, DocNum={DocNum}",
                docEntry, docNum);

            return new SapInvoiceResponse
            {
                DocEntry = docEntry,
                DocNum = docNum,
                ExternalInvoiceId = request.ExternalInvoiceId,
                Lines = lineResponses
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SapIncomingPaymentResponse> UpdateIncomingPaymentAsync(int docEntry, SapIncomingPaymentRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            // When invoice allocation lines are provided, SAP B1 does not allow
            // modifying RCT2 rows on an existing payment.  The only option is to
            // cancel the original "Payment on Account" and create a new payment
            // with the correct invoice allocations.
            if (request.Lines is { Count: > 0 })
            {
                return ReallocateIncomingPaymentCore(docEntry, request);
            }

            _logger.LogInformation(
                "Updating SAP Incoming Payment — DocEntry={DocEntry}, ExternalPaymentId={ExternalPaymentId}",
                docEntry, request.ExternalPaymentId);

            var payment = (Payments)_company!.GetBusinessObject(BoObjectTypes.oIncomingPayments);

            if (!payment.GetByKey(docEntry))
            {
                Marshal.ReleaseComObject(payment);
                throw new InvalidOperationException(
                    $"SAP B1 Incoming Payment with DocEntry={docEntry} not found.");
            }

            // Guard: cancelled payments cannot be updated
            if (payment.Cancelled == BoYesNoEnum.tYES)
            {
                int cancelledDocNum = payment.DocNum;
                Marshal.ReleaseComObject(payment);
                throw new InvalidOperationException(
                    $"SAP B1 Incoming Payment DocEntry={docEntry} (DocNum={cancelledDocNum}) is cancelled. " +
                    "Cannot update a cancelled payment.");
            }

            int docNum = payment.DocNum;

            // Refresh UDFs
            TrySetUserField(payment.UserFields, "U_Odoo_Payment_ID", request.ExternalPaymentId, "Payment update");

            // Document trail: link payment back to the originating SO and invoice
            if (!string.IsNullOrEmpty(request.UOdooSoId))
                TrySetUserField(payment.UserFields, "U_Odoo_SO_ID", request.UOdooSoId, "Payment update");

            if (!string.IsNullOrEmpty(request.ExternalInvoiceId))
                TrySetUserField(payment.UserFields, "U_Odoo_Invoice_ID", request.ExternalInvoiceId, "Payment update");

            var syncDate = DateTime.UtcNow.Date;
            TrySetUserField(payment.UserFields, "U_Odoo_LastSync", syncDate, "Payment update");
            TrySetUserField(payment.UserFields, "U_Odoo_SyncDir", SyncDirectionOdooToSap, "Payment update");

            int result = payment.Update();

            if (result != 0)
            {
                _company.GetLastError(out int errCode, out string errMsg);
                Marshal.ReleaseComObject(payment);
                throw new InvalidOperationException(
                    $"SAP DI API error {errCode}: {errMsg}");
            }

            Marshal.ReleaseComObject(payment);

            _logger.LogInformation(
                "✅ SAP Incoming Payment updated: DocEntry={DocEntry}, DocNum={DocNum}",
                docEntry, docNum);

            return new SapIncomingPaymentResponse
            {
                DocEntry = docEntry,
                DocNum = docNum,
                ExternalPaymentId = request.ExternalPaymentId,
                OdooPaymentId = request.OdooPaymentId
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Cancels an existing Incoming Payment and creates a new one with invoice
    /// allocations.  SAP B1 does not allow modifying the RCT2 (invoice allocation)
    /// table on an existing payment, so this cancel-and-recreate approach is the
    /// standard way to add allocations after the fact.
    /// <para>
    /// Caller must hold <see cref="_lock"/> and call <see cref="EnsureConnected"/> first.
    /// </para>
    /// </summary>
    private SapIncomingPaymentResponse ReallocateIncomingPaymentCore(
        int originalDocEntry, SapIncomingPaymentRequest request)
    {
        _logger.LogInformation(
            "Reallocating SAP Incoming Payment — cancelling DocEntry={DocEntry} " +
            "and recreating with {LineCount} invoice allocation(s)",
            originalDocEntry, request.Lines.Count);

        // Step 1: Cancel the existing "Payment on Account"
        var payment = (Payments)_company!.GetBusinessObject(BoObjectTypes.oIncomingPayments);

        if (!payment.GetByKey(originalDocEntry))
        {
            Marshal.ReleaseComObject(payment);
            throw new InvalidOperationException(
                $"SAP B1 Incoming Payment with DocEntry={originalDocEntry} not found.");
        }

        int cancelResult = payment.Cancel();

        if (cancelResult != 0)
        {
            _company.GetLastError(out int errCode, out string errMsg);
            Marshal.ReleaseComObject(payment);
            throw new InvalidOperationException(
                $"Failed to cancel SAP Incoming Payment DocEntry={originalDocEntry}: " +
                $"DI API error {errCode}: {errMsg}");
        }

        Marshal.ReleaseComObject(payment);

        _logger.LogInformation(
            "Cancelled SAP Incoming Payment DocEntry={DocEntry} — " +
            "creating replacement with invoice allocations",
            originalDocEntry);

        // Step 2: Create a new payment with the full payload (including allocations)
        var newResponse = CreateIncomingPaymentCore(request);

        // Tag the response so the caller knows a reallocation occurred
        newResponse.Reallocated = true;
        newResponse.CancelledDocEntry = originalDocEntry;

        _logger.LogInformation(
            "✅ Payment reallocation complete — old DocEntry={OldDocEntry} → " +
            "new DocEntry={NewDocEntry}, DocNum={NewDocNum}, LineCount={LineCount}, " +
            "TotalApplied={TotalApplied}",
            originalDocEntry,
            newResponse.DocEntry,
            newResponse.DocNum,
            request.Lines.Count,
            newResponse.TotalApplied);

        return newResponse;
    }

    // ================================
    // AR INVOICE STATUS
    // ================================

    public async Task<SapInvoiceStatusResponse> GetInvoiceStatusAsync(int docEntry)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            _logger.LogInformation(
                "Checking AR Invoice status — DocEntry={DocEntry}", docEntry);

            var invoice = (Documents)_company!.GetBusinessObject(BoObjectTypes.oInvoices);

            try
            {
                if (!invoice.GetByKey(docEntry))
                {
                    Marshal.ReleaseComObject(invoice);
                    throw new InvalidOperationException(
                        $"SAP B1 AR Invoice with DocEntry={docEntry} not found.");
                }

                string status = invoice.DocumentStatus == BoStatus.bost_Open ? "open" : "closed";
                int docNum = invoice.DocNum;

                Marshal.ReleaseComObject(invoice);

                _logger.LogInformation(
                    "AR Invoice status: DocEntry={DocEntry}, DocNum={DocNum}, Status={Status}",
                    docEntry, docNum, status);

                return new SapInvoiceStatusResponse
                {
                    DocEntry = docEntry,
                    DocNum = docNum,
                    Status = status
                };
            }
            catch
            {
                Marshal.ReleaseComObject(invoice);
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // ================================
    // AR CREDIT MEMO (ORIN)
    // ================================

    public async Task<SapCreditMemoResponse> CreateCreditMemoAsync(SapCreditMemoRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            int invoiceDocEntry = request.SapBaseInvoiceDocEntry
                ?? throw new InvalidOperationException(
                    "SapBaseInvoiceDocEntry is required. Credit Memos " +
                    "must be created by Copy-From an open AR Invoice.");

            _logger.LogInformation(
                "Creating AR Credit Memo copied from Invoice — " +
                "ExternalCreditMemoId={ExternalCreditMemoId}, " +
                "CustomerCode={CustomerCode}, SapBaseInvoiceDocEntry={InvoiceDocEntry}, " +
                "LineCount={LineCount}",
                request.ExternalCreditMemoId,
                request.CustomerCode,
                invoiceDocEntry,
                request.Lines.Count);

            // ── Load source Invoice from SAP and validate it's open ──
            var invoice = (Documents)_company!.GetBusinessObject(BoObjectTypes.oInvoices);

            if (!invoice.GetByKey(invoiceDocEntry))
            {
                Marshal.ReleaseComObject(invoice);
                throw new InvalidOperationException(
                    $"SAP B1 AR Invoice DocEntry={invoiceDocEntry} not found.");
            }

            if (invoice.DocumentStatus != BoStatus.bost_Open)
            {
                int closedDocNum = invoice.DocNum;
                Marshal.ReleaseComObject(invoice);
                throw new InvalidOperationException(
                    $"SAP B1 AR Invoice DocEntry={invoiceDocEntry} (DocNum={closedDocNum}) " +
                    "is closed. Cannot create a Credit Memo against a closed invoice — " +
                    "reverse the incoming payment in SAP B1 to re-open it first.");
            }

            _logger.LogInformation(
                "Loaded source Invoice: DocEntry={DocEntry}, DocNum={DocNum}, " +
                "CardCode={CardCode}, Status=Open, LineCount={LineCount}",
                invoice.DocEntry, invoice.DocNum, invoice.CardCode,
                invoice.Lines.Count);

            // Build ItemCode → list of (LineNum, Qty, Price, WhsCode) from invoice
            var invoiceLineIndex = new Dictionary<string, List<(int lineNum, double qty, double price, string whsCode)>>();
            for (int s = 0; s < invoice.Lines.Count; s++)
            {
                invoice.Lines.SetCurrentLine(s);
                string itemCode = invoice.Lines.ItemCode;

                _logger.LogInformation(
                    "Invoice Line[{LineNum}]: ItemCode={ItemCode}, Qty={Qty}, " +
                    "OpenQty={OpenQty}, Price={Price}, WhsCode={WhsCode}",
                    invoice.Lines.LineNum, itemCode,
                    invoice.Lines.Quantity, invoice.Lines.RemainingOpenQuantity,
                    invoice.Lines.UnitPrice, invoice.Lines.WarehouseCode);

                if (!invoiceLineIndex.ContainsKey(itemCode))
                    invoiceLineIndex[itemCode] = new List<(int, double, double, string)>();
                invoiceLineIndex[itemCode].Add((
                    invoice.Lines.LineNum,
                    invoice.Lines.Quantity,
                    invoice.Lines.UnitPrice,
                    invoice.Lines.WarehouseCode));
            }
            Marshal.ReleaseComObject(invoice);

            // ── Create Credit Memo (ORIN) ──
            var creditMemo = (Documents)_company!.GetBusinessObject(BoObjectTypes.oCreditNotes);

            try
            {
                // Header
                creditMemo.CardCode = request.CustomerCode;
                creditMemo.NumAtCard = request.ExternalCreditMemoId;

                if (request.DocDate.HasValue)
                    creditMemo.DocDate = request.DocDate.Value;
                if (request.DueDate.HasValue)
                    creditMemo.DocDueDate = request.DueDate.Value;
                if (!string.IsNullOrEmpty(request.Currency))
                    creditMemo.DocCurrency = request.Currency;

                // UDFs
                TrySetUserField(creditMemo.UserFields, "U_Odoo_Invoice_ID",
                    request.ExternalCreditMemoId, "Credit Memo header");
                if (!string.IsNullOrEmpty(request.UOdooSoId))
                    TrySetUserField(creditMemo.UserFields, "U_Odoo_SO_ID",
                        request.UOdooSoId, "Credit Memo header");
                var syncDate = DateTime.UtcNow.Date;
                TrySetUserField(creditMemo.UserFields, "U_Odoo_LastSync",
                    syncDate, "Credit Memo header");
                TrySetUserField(creditMemo.UserFields, "U_Odoo_SyncDir",
                    SyncDirectionOdooToSap, "Credit Memo header");

                // Lines — Copy-From Invoice
                for (int i = 0; i < request.Lines.Count; i++)
                {
                    if (i > 0)
                        creditMemo.Lines.Add();

                    var line = request.Lines[i];

                    // Match credit line to invoice line by ItemCode
                    if (!invoiceLineIndex.TryGetValue(line.ItemCode, out var candidates)
                        || candidates.Count == 0)
                    {
                        throw new InvalidOperationException(
                            $"Credit Memo line[{i}] ItemCode={line.ItemCode} not found " +
                            $"on Invoice DocEntry={invoiceDocEntry}.");
                    }

                    var match = candidates[0];
                    candidates.RemoveAt(0);

                    creditMemo.Lines.ItemCode = line.ItemCode;
                    creditMemo.Lines.Quantity = line.Quantity;
                    creditMemo.Lines.UnitPrice = line.Price;

                    if (line.DiscountPercent.HasValue)
                        creditMemo.Lines.DiscountPercent = line.DiscountPercent.Value;

                    if (!string.IsNullOrEmpty(line.WarehouseCode))
                        creditMemo.Lines.WarehouseCode = line.WarehouseCode;

                    // Copy-From Invoice (BaseType=13)
                    creditMemo.Lines.BaseType = (int)BoObjectTypes.oInvoices;
                    creditMemo.Lines.BaseEntry = invoiceDocEntry;
                    creditMemo.Lines.BaseLine = match.lineNum;

                    _logger.LogInformation(
                        "Credit Memo Line[{Index}]: ItemCode={ItemCode}, Qty={Qty}, " +
                        "Price={Price}, BaseEntry={BaseEntry}, BaseLine={BaseLine}",
                        i, line.ItemCode, line.Quantity, line.Price,
                        invoiceDocEntry, match.lineNum);
                }

                int result = creditMemo.Add();

                if (result != 0)
                {
                    _company!.GetLastError(out int errCode, out string errMsg);

                    _logger.LogError(
                        "Failed to create AR Credit Memo for {ExternalCreditMemoId}: " +
                        "DI API error {ErrCode}: {ErrMsg}",
                        request.ExternalCreditMemoId, errCode, errMsg);

                    throw new InvalidOperationException(
                        $"SAP DI API error {errCode}: {errMsg}");
                }

                int docEntry = int.Parse(_company!.GetNewObjectKey());
                creditMemo.GetByKey(docEntry);
                int docNum = creditMemo.DocNum;

                _logger.LogInformation(
                    "AR Credit Memo created: DocEntry={DocEntry}, DocNum={DocNum}, " +
                    "ExternalCreditMemoId={ExternalCreditMemoId}, " +
                    "BaseInvoiceDocEntry={BaseInvoiceDocEntry}, LineCount={LineCount}",
                    docEntry, docNum, request.ExternalCreditMemoId,
                    invoiceDocEntry, request.Lines.Count);

                return new SapCreditMemoResponse
                {
                    DocEntry = docEntry,
                    DocNum = docNum,
                    ExternalCreditMemoId = request.ExternalCreditMemoId,
                    OdooInvoiceId = request.OdooInvoiceId
                };
            }
            finally
            {
                Marshal.ReleaseComObject(creditMemo);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SapCreditMemoResponse> UpdateCreditMemoAsync(int docEntry, SapCreditMemoRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            _logger.LogInformation(
                "Updating SAP AR Credit Memo — DocEntry={DocEntry}, ExternalCreditMemoId={ExternalCreditMemoId}",
                docEntry, request.ExternalCreditMemoId);

            var creditMemo = (Documents)_company!.GetBusinessObject(BoObjectTypes.oCreditNotes);

            if (!creditMemo.GetByKey(docEntry))
            {
                Marshal.ReleaseComObject(creditMemo);
                throw new InvalidOperationException(
                    $"SAP B1 AR Credit Memo with DocEntry={docEntry} not found.");
            }

            // If document is closed, skip update gracefully.
            if (creditMemo.DocumentStatus != BoStatus.bost_Open)
            {
                int closedDocNum = creditMemo.DocNum;
                Marshal.ReleaseComObject(creditMemo);

                _logger.LogInformation(
                    "SAP AR Credit Memo DocEntry={DocEntry} (DocNum={DocNum}) is closed — " +
                    "skipping UDF update.",
                    docEntry, closedDocNum);

                return new SapCreditMemoResponse
                {
                    DocEntry = docEntry,
                    DocNum = closedDocNum,
                    ExternalCreditMemoId = request.ExternalCreditMemoId,
                    OdooInvoiceId = request.OdooInvoiceId
                };
            }

            int docNum = creditMemo.DocNum;

            // Refresh UDFs
            TrySetUserField(creditMemo.UserFields, "U_Odoo_Invoice_ID", request.ExternalCreditMemoId, "Credit Memo update");

            if (!string.IsNullOrEmpty(request.UOdooSoId))
                TrySetUserField(creditMemo.UserFields, "U_Odoo_SO_ID", request.UOdooSoId, "Credit Memo update");

            var syncDate = DateTime.UtcNow.Date;
            TrySetUserField(creditMemo.UserFields, "U_Odoo_LastSync", syncDate, "Credit Memo update");
            TrySetUserField(creditMemo.UserFields, "U_Odoo_SyncDir", SyncDirectionOdooToSap, "Credit Memo update");

            int result = creditMemo.Update();

            if (result != 0)
            {
                _company.GetLastError(out int errCode, out string errMsg);
                Marshal.ReleaseComObject(creditMemo);
                throw new InvalidOperationException(
                    $"SAP DI API error {errCode}: {errMsg}");
            }

            Marshal.ReleaseComObject(creditMemo);

            _logger.LogInformation(
                "✅ SAP AR Credit Memo updated: DocEntry={DocEntry}, DocNum={DocNum}",
                docEntry, docNum);

            return new SapCreditMemoResponse
            {
                DocEntry = docEntry,
                DocNum = docNum,
                ExternalCreditMemoId = request.ExternalCreditMemoId,
                OdooInvoiceId = request.OdooInvoiceId
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    // ================================
    // DELIVERY NOTE STATUS (ODLN)
    // ================================

    public async Task<SapDeliveryStatusResponse> GetDeliveryStatusAsync(int docEntry)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            _logger.LogInformation(
                "Checking Delivery Note status — DocEntry={DocEntry}", docEntry);

            var delivery = (Documents)_company!.GetBusinessObject(BoObjectTypes.oDeliveryNotes);

            try
            {
                if (!delivery.GetByKey(docEntry))
                {
                    Marshal.ReleaseComObject(delivery);
                    throw new InvalidOperationException(
                        $"SAP B1 Delivery Note with DocEntry={docEntry} not found.");
                }

                string status = delivery.DocumentStatus == BoStatus.bost_Open ? "open" : "closed";
                int docNum = delivery.DocNum;

                Marshal.ReleaseComObject(delivery);

                _logger.LogInformation(
                    "Delivery Note status: DocEntry={DocEntry}, DocNum={DocNum}, Status={Status}",
                    docEntry, docNum, status);

                return new SapDeliveryStatusResponse
                {
                    DocEntry = docEntry,
                    DocNum = docNum,
                    Status = status
                };
            }
            catch
            {
                Marshal.ReleaseComObject(delivery);
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Reads a SAP Delivery Note and returns all unique Odoo SO
    /// references from the base documents (one per line's BaseEntry).
    /// Used for multi-SO delivery callback handling.
    /// </summary>
    public async Task<List<string>> ReadDeliveryBaseSoRefsAsync(int docEntry)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            var delivery = (Documents)_company!.GetBusinessObject(BoObjectTypes.oDeliveryNotes);

            try
            {
                if (!delivery.GetByKey(docEntry))
                {
                    Marshal.ReleaseComObject(delivery);
                    return [];
                }

                var soDocEntries = new HashSet<int>();
                int lineCount = delivery.Lines.Count;
                for (int i = 0; i < lineCount; i++)
                {
                    delivery.Lines.SetCurrentLine(i);
                    if (delivery.Lines.BaseType == (int)BoObjectTypes.oOrders
                        && delivery.Lines.BaseEntry > 0)
                    {
                        soDocEntries.Add(delivery.Lines.BaseEntry);
                    }
                }

                var soRefs = new List<string>();
                foreach (int soDocEntry in soDocEntries)
                {
                    var so = (Documents)_company.GetBusinessObject(BoObjectTypes.oOrders);
                    try
                    {
                        if (so.GetByKey(soDocEntry))
                        {
                            string odooRef = "";
                            try { odooRef = so.UserFields.Fields.Item("U_Odoo_SO_ID").Value?.ToString() ?? ""; }
                            catch { /* UDF not found */ }
                            if (!string.IsNullOrEmpty(odooRef))
                                soRefs.Add(odooRef);
                        }
                    }
                    finally { Marshal.ReleaseComObject(so); }
                }

                Marshal.ReleaseComObject(delivery);

                _logger.LogInformation(
                    "Delivery DocEntry={DocEntry}: found {Count} base SO refs: [{Refs}]",
                    docEntry, soRefs.Count, string.Join(", ", soRefs));

                return soRefs;
            }
            catch
            {
                Marshal.ReleaseComObject(delivery);
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // ================================
    // RETURN REQUEST (ORRR)
    // ================================

    public async Task<SapReturnRequestStatusResponse> GetReturnRequestStatusAsync(int docEntry)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            _logger.LogInformation(
                "Checking Return Request status — DocEntry={DocEntry}", docEntry);

            var returnReq = (Documents)_company!.GetBusinessObject(BoObjectTypes.oReturnRequest);

            try
            {
                if (!returnReq.GetByKey(docEntry))
                {
                    Marshal.ReleaseComObject(returnReq);
                    throw new InvalidOperationException(
                        $"SAP B1 Return Request with DocEntry={docEntry} not found.");
                }

                string status = returnReq.DocumentStatus == BoStatus.bost_Open ? "open" : "closed";
                int docNum = returnReq.DocNum;

                Marshal.ReleaseComObject(returnReq);

                _logger.LogInformation(
                    "Return Request status: DocEntry={DocEntry}, DocNum={DocNum}, Status={Status}",
                    docEntry, docNum, status);

                return new SapReturnRequestStatusResponse
                {
                    DocEntry = docEntry,
                    DocNum = docNum,
                    Status = status
                };
            }
            catch
            {
                Marshal.ReleaseComObject(returnReq);
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SapGoodsReturnResponse> CreateGoodsReturnAsync(SapGoodsReturnRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            int deliveryDocEntry = request.SapBaseDeliveryDocEntry
                ?? throw new InvalidOperationException(
                    "SapBaseDeliveryDocEntry is required. Goods Returns " +
                    "must be created by Copy-From a Delivery Note.");

            _logger.LogInformation(
                "Creating Goods Return (ORDN) copied from Delivery — " +
                "ExternalReturnId={ExternalReturnId}, CustomerCode={CustomerCode}, " +
                "SapBaseDeliveryDocEntry={DeliveryDocEntry}, LineCount={LineCount}",
                request.ExternalReturnId,
                request.CustomerCode,
                deliveryDocEntry,
                request.Lines.Count);

            // ── Load source Delivery from SAP ──
            var delivery = (Documents)_company!.GetBusinessObject(
                BoObjectTypes.oDeliveryNotes);

            if (!delivery.GetByKey(deliveryDocEntry))
            {
                Marshal.ReleaseComObject(delivery);
                throw new InvalidOperationException(
                    $"SAP B1 Delivery Note DocEntry={deliveryDocEntry} not found.");
            }

            _logger.LogInformation(
                "Loaded source Delivery: DocEntry={DocEntry}, DocNum={DocNum}, " +
                "CardCode={CardCode}, Status={Status}, LineCount={LineCount}",
                delivery.DocEntry, delivery.DocNum, delivery.CardCode,
                delivery.DocumentStatus, delivery.Lines.Count);

            // Build ItemCode → list of (LineNum, Qty, WhsCode) from delivery
            var deliveryLineIndex = new Dictionary<string, List<(int lineNum, double qty, string whsCode)>>();
            for (int d = 0; d < delivery.Lines.Count; d++)
            {
                delivery.Lines.SetCurrentLine(d);
                string itemCode = delivery.Lines.ItemCode;

                _logger.LogInformation(
                    "Delivery Line[{LineNum}]: ItemCode={ItemCode}, Qty={Qty}, " +
                    "OpenQty={OpenQty}, WhsCode={WhsCode}",
                    delivery.Lines.LineNum, itemCode,
                    delivery.Lines.Quantity, delivery.Lines.RemainingOpenQuantity,
                    delivery.Lines.WarehouseCode);

                if (!deliveryLineIndex.ContainsKey(itemCode))
                    deliveryLineIndex[itemCode] = new List<(int, double, string)>();
                deliveryLineIndex[itemCode].Add((
                    delivery.Lines.LineNum,
                    delivery.Lines.Quantity,
                    delivery.Lines.WarehouseCode));
            }
            Marshal.ReleaseComObject(delivery);

            // ── Pre-match return lines to delivery lines by ItemCode ──
            var matchedLines = new List<(SapGoodsReturnLineRequest line, int baseLineNum, string whsCode)>();
            var matchIndex = deliveryLineIndex.ToDictionary(
                kvp => kvp.Key,
                kvp => new List<(int lineNum, double qty, string whsCode)>(kvp.Value));

            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                if (!matchIndex.TryGetValue(line.ItemCode, out var candidates)
                    || candidates.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Goods Return line[{i}] ItemCode={line.ItemCode} not found " +
                        $"on Delivery DocEntry={deliveryDocEntry}.");
                }

                var match = candidates[0];
                candidates.RemoveAt(0);

                string whsCode = !string.IsNullOrEmpty(line.WarehouseCode)
                    ? line.WarehouseCode
                    : match.whsCode;
                matchedLines.Add((line, match.lineNum, whsCode));
            }

            // ── Attempt 1: Copy-From Delivery (ORDN) ──
            var goodsReturn = (Documents)_company!.GetBusinessObject(
                BoObjectTypes.oReturns);

            SetGoodsReturnHeader(goodsReturn, request);

            for (int i = 0; i < matchedLines.Count; i++)
            {
                if (i > 0)
                    goodsReturn.Lines.Add();

                var (line, baseLineNum, whsCode) = matchedLines[i];

                goodsReturn.Lines.ItemCode = line.ItemCode;
                goodsReturn.Lines.Quantity = line.Quantity;
                goodsReturn.Lines.WarehouseCode = whsCode;

                // Copy-From Delivery (BaseType=15)
                goodsReturn.Lines.BaseType = (int)BoObjectTypes.oDeliveryNotes;
                goodsReturn.Lines.BaseEntry = deliveryDocEntry;
                goodsReturn.Lines.BaseLine = baseLineNum;

                _logger.LogInformation(
                    "Goods Return Line[{Index}]: ItemCode={ItemCode}, Qty={Qty}, " +
                    "BaseEntry={BaseEntry}, BaseLine={BaseLine}, WhsCode={WhsCode}",
                    i, line.ItemCode, line.Quantity,
                    deliveryDocEntry, baseLineNum, whsCode);
            }

            int result = goodsReturn.Add();

            if (result != 0)
            {
                _company!.GetLastError(out int errCode, out string errMsg);
                Marshal.ReleaseComObject(goodsReturn);

                _logger.LogError(
                    "Failed to create Goods Return for {ExternalReturnId}: " +
                    "DI API error {ErrCode}: {ErrMsg}",
                    request.ExternalReturnId, errCode, errMsg);

                throw new InvalidOperationException(
                    $"SAP DI API error {errCode}: {errMsg}");
            }

            int docEntry = int.Parse(_company!.GetNewObjectKey());

            goodsReturn.GetByKey(docEntry);
            int docNum = goodsReturn.DocNum;

            Marshal.ReleaseComObject(goodsReturn);

            _logger.LogInformation(
                "Goods Return (ORDN) created: DocEntry={DocEntry}, DocNum={DocNum}, " +
                "ExternalReturnId={ExternalReturnId}, LineCount={LineCount}",
                docEntry, docNum, request.ExternalReturnId, request.Lines.Count);

            return new SapGoodsReturnResponse
            {
                DocEntry = docEntry,
                DocNum = docNum,
                ExternalReturnId = request.ExternalReturnId,
                OdooPickingId = request.OdooPickingId
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Sets header fields and UDFs on a return request document.
    /// </summary>
    private void SetGoodsReturnHeader(Documents returnDoc, SapGoodsReturnRequest request)
    {
        returnDoc.CardCode = request.CustomerCode;

        if (request.DeliveryDate.HasValue)
            returnDoc.DocDate = request.DeliveryDate.Value;

        TrySetUserField(returnDoc.UserFields, "U_Odoo_Delivery_ID", request.ExternalReturnId, "Return Request header");

        if (!string.IsNullOrEmpty(request.UOdooSoId))
            TrySetUserField(returnDoc.UserFields, "U_Odoo_SO_ID", request.UOdooSoId, "Return Request header");

        var syncDate = DateTime.UtcNow.Date;
        TrySetUserField(returnDoc.UserFields, "U_Odoo_LastSync", syncDate, "Return Request header");
        TrySetUserField(returnDoc.UserFields, "U_Odoo_SyncDir", SyncDirectionOdooToSap, "Return Request header");
    }

    public async Task<SapGoodsReturnResponse> UpdateGoodsReturnAsync(int docEntry, SapGoodsReturnRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            _logger.LogInformation(
                "Updating SAP Return Request — DocEntry={DocEntry}, ExternalReturnId={ExternalReturnId}",
                docEntry, request.ExternalReturnId);

            var goodsReturn = (Documents)_company!.GetBusinessObject(BoObjectTypes.oReturnRequest);

            if (!goodsReturn.GetByKey(docEntry))
            {
                Marshal.ReleaseComObject(goodsReturn);
                throw new InvalidOperationException(
                    $"SAP B1 Goods Return with DocEntry={docEntry} not found.");
            }

            // If document is closed, skip update gracefully.
            if (goodsReturn.DocumentStatus != BoStatus.bost_Open)
            {
                int closedDocNum = goodsReturn.DocNum;
                Marshal.ReleaseComObject(goodsReturn);

                _logger.LogInformation(
                    "SAP Goods Return DocEntry={DocEntry} (DocNum={DocNum}) is closed — " +
                    "skipping UDF update.",
                    docEntry, closedDocNum);

                return new SapGoodsReturnResponse
                {
                    DocEntry = docEntry,
                    DocNum = closedDocNum,
                    ExternalReturnId = request.ExternalReturnId,
                    OdooPickingId = request.OdooPickingId
                };
            }

            int docNum = goodsReturn.DocNum;

            // Refresh UDFs
            TrySetUserField(goodsReturn.UserFields, "U_Odoo_Delivery_ID", request.ExternalReturnId, "Goods Return update");

            if (!string.IsNullOrEmpty(request.UOdooSoId))
                TrySetUserField(goodsReturn.UserFields, "U_Odoo_SO_ID", request.UOdooSoId, "Goods Return update");

            var syncDate = DateTime.UtcNow.Date;
            TrySetUserField(goodsReturn.UserFields, "U_Odoo_LastSync", syncDate, "Goods Return update");
            TrySetUserField(goodsReturn.UserFields, "U_Odoo_SyncDir", SyncDirectionOdooToSap, "Goods Return update");

            int result = goodsReturn.Update();

            if (result != 0)
            {
                _company.GetLastError(out int errCode, out string errMsg);
                Marshal.ReleaseComObject(goodsReturn);
                throw new InvalidOperationException(
                    $"SAP DI API error {errCode}: {errMsg}");
            }

            Marshal.ReleaseComObject(goodsReturn);

            _logger.LogInformation(
                "✅ SAP Goods Return updated: DocEntry={DocEntry}, DocNum={DocNum}",
                docEntry, docNum);

            return new SapGoodsReturnResponse
            {
                DocEntry = docEntry,
                DocNum = docNum,
                ExternalReturnId = request.ExternalReturnId,
                OdooPickingId = request.OdooPickingId
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CancelGoodsReturnAsync(int docEntry)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            _logger.LogInformation(
                "Cancelling SAP Goods Return — DocEntry={DocEntry}",
                docEntry);

            var goodsReturn = (Documents)_company!.GetBusinessObject(
                BoObjectTypes.oReturns);

            if (!goodsReturn.GetByKey(docEntry))
            {
                Marshal.ReleaseComObject(goodsReturn);
                throw new InvalidOperationException(
                    $"SAP Goods Return DocEntry={docEntry} not found.");
            }

            // If already closed/cancelled, skip
            if (goodsReturn.DocumentStatus != BoStatus.bost_Open)
            {
                Marshal.ReleaseComObject(goodsReturn);
                _logger.LogInformation(
                    "SAP Goods Return DocEntry={DocEntry} is already "
                    + "closed — no cancellation needed.", docEntry);
                return;
            }

            int result = goodsReturn.Cancel();

            if (result != 0)
            {
                _company!.GetLastError(out int errCode, out string errMsg);
                Marshal.ReleaseComObject(goodsReturn);
                throw new InvalidOperationException(
                    $"SAP DI API error {errCode}: {errMsg}");
            }

            Marshal.ReleaseComObject(goodsReturn);

            _logger.LogInformation(
                "✅ SAP Goods Return cancelled: DocEntry={DocEntry}",
                docEntry);
        }
        finally
        {
            _lock.Release();
        }
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
    // ═══════════════════════════════════════════════════════════════════
    //  Document Lookup by Odoo Reference (UDF search)
    // ═══════════════════════════════════════════════════════════════════

    public async Task<SapDocumentLookupResponse?> LookupDocumentAsync(
        string documentType, string odooRef)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            _logger.LogInformation(
                "Looking up SAP document — type={DocumentType}, odooRef={OdooRef}",
                documentType, odooRef);

            // Determine SAP table and UDF based on document type
            var (table, udfColumn, statusColumn) = documentType switch
            {
                "sales-order" => ("ORDR", "U_Odoo_SO_ID", "DocStatus"),
                "delivery"    => ("ODLN", "U_Odoo_Delivery_ID", "DocStatus"),
                "invoice"     => ("OINV", "U_Odoo_Invoice_ID", "DocStatus"),
                "payment"     => ("ORCT", "U_Odoo_Payment_ID", "Canceled"),
                "return"      => ("ORDN", "U_Odoo_Delivery_ID", "DocStatus"),
                "credit-memo" => ("ORIN", "U_Odoo_Invoice_ID", "DocStatus"),
                _ => throw new InvalidOperationException(
                    $"Unknown document type: {documentType}")
            };

            // Query SAP for the document by UDF
            var rs = (Recordset)_company!.GetBusinessObject(
                BoObjectTypes.BoRecordset);

            string sql = $"SELECT T0.\"DocEntry\", T0.\"DocNum\", " +
                         $"T0.\"{statusColumn}\", T0.\"CardCode\" " +
                         $"FROM \"{table}\" T0 " +
                         $"WHERE T0.\"{udfColumn}\" = '{odooRef.Replace("'", "''")}'";

            rs.DoQuery(sql);

            if (rs.EoF)
            {
                Marshal.ReleaseComObject(rs);
                _logger.LogInformation(
                    "No SAP document found for type={DocumentType}, " +
                    "odooRef={OdooRef}", documentType, odooRef);
                return null;
            }

            int docEntry = (int)rs.Fields.Item("DocEntry").Value;
            int docNum = (int)rs.Fields.Item("DocNum").Value;
            string cardCode = (string)rs.Fields.Item("CardCode").Value;

            // Parse status — payments use "Canceled" (Y/N), others use DocStatus (O/C)
            string status;
            if (documentType == "payment")
            {
                string canceled = (string)rs.Fields.Item("Canceled").Value;
                status = canceled == "Y" ? "cancelled" : "active";
            }
            else
            {
                string docStatus = (string)rs.Fields.Item("DocStatus").Value;
                status = docStatus == "O" ? "open" : "closed";
            }

            Marshal.ReleaseComObject(rs);

            // Look up pick list entry for sales orders and deliveries
            int? pickListEntry = null;
            if (documentType == "sales-order")
            {
                pickListEntry = LookupPickListForSalesOrder(docEntry);
            }
            else if (documentType == "delivery")
            {
                pickListEntry = LookupPickListForDelivery(docEntry);
            }

            _logger.LogInformation(
                "SAP document found — type={DocumentType}, odooRef={OdooRef}, " +
                "DocEntry={DocEntry}, DocNum={DocNum}, Status={Status}, " +
                "PickListEntry={PickListEntry}",
                documentType, odooRef, docEntry, docNum, status, pickListEntry);

            return new SapDocumentLookupResponse
            {
                DocEntry = docEntry,
                DocNum = docNum,
                Status = status,
                CardCode = cardCode,
                OdooRef = odooRef,
                PickListEntry = pickListEntry
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    // ================================
    // READ INVOICE COSTS (for COGS retry)
    // ================================
    public async Task<SapInvoiceResponse> ReadInvoiceCostsAsync(int docEntry)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            _logger.LogInformation(
                "Reading AR Invoice costs from SAP — DocEntry={DocEntry}", docEntry);

            var invoice = (Documents)_company!.GetBusinessObject(BoObjectTypes.oInvoices);

            if (!invoice.GetByKey(docEntry))
            {
                Marshal.ReleaseComObject(invoice);
                throw new InvalidOperationException(
                    $"SAP B1 AR Invoice with DocEntry={docEntry} not found.");
            }

            int docNum = invoice.DocNum;
            var lines = ReadInvoiceLines(invoice);

            Marshal.ReleaseComObject(invoice);

            _logger.LogInformation(
                "AR Invoice costs read — DocEntry={DocEntry}, DocNum={DocNum}, LineCount={LineCount}",
                docEntry, docNum, lines.Count);

            return new SapInvoiceResponse
            {
                DocEntry = docEntry,
                DocNum = docNum,
                Lines = lines
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    private int? LookupPickListForSalesOrder(int soDocEntry)
    {
        try
        {
            var rs = (Recordset)_company!.GetBusinessObject(
                BoObjectTypes.BoRecordset);

            rs.DoQuery(
                $"SELECT DISTINCT T0.\"AbsEntry\" " +
                $"FROM \"PKL1\" T0 " +
                $"WHERE T0.\"OrderEntry\" = {soDocEntry} " +
                $"AND T0.\"BaseObject\" = '17'");

            if (rs.EoF)
            {
                Marshal.ReleaseComObject(rs);
                return null;
            }

            int absEntry = (int)rs.Fields.Item("AbsEntry").Value;
            Marshal.ReleaseComObject(rs);
            return absEntry;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to look up pick list for SO DocEntry={DocEntry}",
                soDocEntry);
            return null;
        }
    }

    private int? LookupPickListForDelivery(int deliveryDocEntry)
    {
        try
        {
            var rs = (Recordset)_company!.GetBusinessObject(
                BoObjectTypes.BoRecordset);

            // Trace delivery → base SO → pick list
            rs.DoQuery(
                $"SELECT DISTINCT T1.\"AbsEntry\" " +
                $"FROM \"DLN1\" T0 " +
                $"INNER JOIN \"PKL1\" T1 " +
                $"ON T1.\"OrderEntry\" = T0.\"BaseEntry\" " +
                $"AND T1.\"BaseObject\" = '17' " +
                $"WHERE T0.\"DocEntry\" = {deliveryDocEntry} " +
                $"AND T0.\"BaseType\" = 17");

            if (rs.EoF)
            {
                Marshal.ReleaseComObject(rs);
                return null;
            }

            int absEntry = (int)rs.Fields.Item("AbsEntry").Value;
            Marshal.ReleaseComObject(rs);
            return absEntry;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to look up pick list for Delivery DocEntry={DocEntry}",
                deliveryDocEntry);
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Private helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to set a User-Defined Field (UDF) on the given <paramref name="userFields"/> object.
    /// Logs a warning (instead of throwing) when the field does not exist in the SAP B1 schema.
    /// </summary>
    /// <returns><c>true</c> if the field was set successfully; <c>false</c> otherwise.</returns>
    private bool TrySetUserField(UserFields userFields, string fieldName, object value, string context)
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

    // ================================
    // CUSTOMERS (BUSINESS PARTNERS)
    // ================================

    public async Task<SapCustomerResponse> CreateCustomerAsync(SapCustomerRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            var bp = (BusinessPartners)_company!.GetBusinessObject(BoObjectTypes.oBusinessPartners);

            // SAP B1 requires CardCode (PK) — generate from Odoo customer ID
            var cardCode = $"C{request.OdooCustomerId}";
            bp.CardCode = cardCode;
            bp.CardName = request.CardName;
            bp.CardType = BoCardTypes.cCustomer;
            bp.Phone1 = request.Phone1;
            bp.GroupCode = request.GroupCode;

            if (request.SlpCode.HasValue && request.SlpCode.Value >= 0)
                bp.SalesPersonCode = request.SlpCode.Value;

            if (request.PriceListNum.HasValue)
                bp.PriceListNum = request.PriceListNum.Value;

            if (!string.IsNullOrEmpty(request.Phone2))
                bp.Phone2 = request.Phone2;

            if (!string.IsNullOrEmpty(request.Email))
                bp.EmailAddress = request.Email;

            TrySetUserField(bp.UserFields, "U_OdooCustomerId", request.OdooCustomerId, "BP header");

            var syncDate = DateTime.UtcNow.Date;
            TrySetUserField(bp.UserFields, "U_Odoo_LastSync", syncDate, "BP header");
            TrySetUserField(bp.UserFields, "U_Odoo_SyncDir", SyncDirectionOdooToSap, "BP header");

            // Bill-to address
            if (request.BillTo != null)
            {
                bp.Addresses.AddressName = "Bill To";
                bp.Addresses.AddressType = BoAddressType.bo_BillTo;
                bp.Addresses.Street = request.BillTo.Street;
                if (!string.IsNullOrEmpty(request.BillTo.City))
                    bp.Addresses.City = request.BillTo.City;
                if (!string.IsNullOrEmpty(request.BillTo.Country))
                    bp.Addresses.Country = request.BillTo.Country;
                if (!string.IsNullOrEmpty(request.BillTo.ZipCode))
                    bp.Addresses.ZipCode = request.BillTo.ZipCode;
                if (!string.IsNullOrEmpty(request.BillTo.State))
                    bp.Addresses.State = request.BillTo.State;
                bp.Addresses.Add();
            }

            // Ship-to address
            if (request.ShipTo != null)
            {
                bp.Addresses.AddressName = "Ship To";
                bp.Addresses.AddressType = BoAddressType.bo_ShipTo;
                bp.Addresses.Street = request.ShipTo.Street;
                if (!string.IsNullOrEmpty(request.ShipTo.City))
                    bp.Addresses.City = request.ShipTo.City;
                if (!string.IsNullOrEmpty(request.ShipTo.Country))
                    bp.Addresses.Country = request.ShipTo.Country;
                if (!string.IsNullOrEmpty(request.ShipTo.ZipCode))
                    bp.Addresses.ZipCode = request.ShipTo.ZipCode;
                if (!string.IsNullOrEmpty(request.ShipTo.State))
                    bp.Addresses.State = request.ShipTo.State;
                bp.Addresses.Add();
            }

            int result = bp.Add();

            if (result != 0)
            {
                _company.GetLastError(out int errCode, out string errMsg);
                Marshal.ReleaseComObject(bp);
                throw new InvalidOperationException(
                    $"SAP DI API error {errCode}: {errMsg}");
            }

            // cardCode already set above — GetNewObjectKey returns same value
            var sapCardCode = _company.GetNewObjectKey();

            _logger.LogInformation(
                "SAP Customer created: CardCode={CardCode}, CardName={CardName}, OdooId={OdooId}",
                cardCode, request.CardName, request.OdooCustomerId);

            Marshal.ReleaseComObject(bp);

            return new SapCustomerResponse
            {
                CardCode = cardCode,
                CardName = request.CardName,
                OdooCustomerId = request.OdooCustomerId,
                Operation = "created"
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SapCustomerResponse> UpdateCustomerAsync(string cardCode, SapCustomerRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            var bp = (BusinessPartners)_company!.GetBusinessObject(BoObjectTypes.oBusinessPartners);

            if (!bp.GetByKey(cardCode))
            {
                Marshal.ReleaseComObject(bp);
                throw new InvalidOperationException(
                    $"Customer '{cardCode}' not found in SAP B1");
            }

            bp.CardName = request.CardName;
            bp.Phone1 = request.Phone1;

            if (request.SlpCode.HasValue && request.SlpCode.Value >= 0)
                bp.SalesPersonCode = request.SlpCode.Value;

            if (request.PriceListNum.HasValue)
                bp.PriceListNum = request.PriceListNum.Value;

            if (!string.IsNullOrEmpty(request.Phone2))
                bp.Phone2 = request.Phone2;

            if (!string.IsNullOrEmpty(request.Email))
                bp.EmailAddress = request.Email;

            if (request.GroupCode > 0)
                bp.GroupCode = request.GroupCode;

            var syncDate = DateTime.UtcNow.Date;
            TrySetUserField(bp.UserFields, "U_Odoo_LastSync", syncDate, "BP header");
            TrySetUserField(bp.UserFields, "U_Odoo_SyncDir", SyncDirectionOdooToSap, "BP header");

            int result = bp.Update();

            if (result != 0)
            {
                _company.GetLastError(out int errCode, out string errMsg);
                Marshal.ReleaseComObject(bp);
                throw new InvalidOperationException(
                    $"SAP DI API error {errCode}: {errMsg}");
            }

            _logger.LogInformation(
                "SAP Customer updated: CardCode={CardCode}, CardName={CardName}, OdooId={OdooId}",
                cardCode, request.CardName, request.OdooCustomerId);

            Marshal.ReleaseComObject(bp);

            return new SapCustomerResponse
            {
                CardCode = cardCode,
                CardName = request.CardName,
                OdooCustomerId = request.OdooCustomerId,
                Operation = "updated"
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    // ================================
    // SALES EMPLOYEES (OSLP)
    // ================================

    public async Task<SapSalesEmployeeResponse> CreateSalesEmployeeAsync(SapSalesEmployeeRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            var sp = (SalesPersons)_company!.GetBusinessObject(BoObjectTypes.oSalesPersons);

            sp.SalesEmployeeName = request.SlpName;

            TrySetUserField(sp.UserFields, "U_OdooEmployeeId", request.OdooEmployeeId, "SalesPerson header");

            int result = sp.Add();

            if (result != 0)
            {
                _company.GetLastError(out int errCode, out string errMsg);
                Marshal.ReleaseComObject(sp);
                throw new InvalidOperationException(
                    $"SAP DI API error {errCode}: {errMsg}");
            }

            string newKey = _company.GetNewObjectKey();
            int slpCode = int.Parse(newKey);

            _logger.LogInformation(
                "SAP Sales Employee created: SlpCode={SlpCode}, SlpName={SlpName}, OdooId={OdooId}",
                slpCode, request.SlpName, request.OdooEmployeeId);

            Marshal.ReleaseComObject(sp);

            return new SapSalesEmployeeResponse
            {
                SlpCode = slpCode,
                SlpName = request.SlpName,
                OdooEmployeeId = request.OdooEmployeeId,
                Operation = "created"
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SapSalesEmployeeResponse> UpdateSalesEmployeeAsync(int slpCode, SapSalesEmployeeRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            var sp = (SalesPersons)_company!.GetBusinessObject(BoObjectTypes.oSalesPersons);

            if (!sp.GetByKey(slpCode))
            {
                Marshal.ReleaseComObject(sp);
                throw new InvalidOperationException(
                    $"Sales Employee SlpCode={slpCode} not found in SAP B1");
            }

            if (!string.IsNullOrEmpty(request.SlpName))
                sp.SalesEmployeeName = request.SlpName;

            TrySetUserField(sp.UserFields, "U_OdooEmployeeId", request.OdooEmployeeId, "SalesPerson header");

            int result = sp.Update();

            if (result != 0)
            {
                _company.GetLastError(out int errCode, out string errMsg);
                Marshal.ReleaseComObject(sp);
                throw new InvalidOperationException(
                    $"SAP DI API error {errCode}: {errMsg}");
            }

            _logger.LogInformation(
                "SAP Sales Employee updated: SlpCode={SlpCode}, SlpName={SlpName}, OdooId={OdooId}",
                slpCode, request.SlpName, request.OdooEmployeeId);

            Marshal.ReleaseComObject(sp);

            return new SapSalesEmployeeResponse
            {
                SlpCode = slpCode,
                SlpName = request.SlpName,
                OdooEmployeeId = request.OdooEmployeeId,
                Operation = "updated"
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<SapSalesEmployeeResponse>> ListSalesEmployeesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();

            var employees = new List<SapSalesEmployeeResponse>();
            var recordset = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);

            recordset.DoQuery(
                "SELECT T0.\"SlpCode\", T0.\"SlpName\", T0.\"U_OdooEmployeeId\" " +
                "FROM \"OSLP\" T0 WHERE T0.\"Active\" = 'Y' ORDER BY T0.\"SlpCode\"");

            while (!recordset.EoF)
            {
                var emp = new SapSalesEmployeeResponse
                {
                    SlpCode = (int)recordset.Fields.Item("SlpCode").Value,
                    SlpName = (string)recordset.Fields.Item("SlpName").Value,
                    OdooEmployeeId = "",
                    Operation = "listed"
                };

                try
                {
                    var odooIdVal = recordset.Fields.Item("U_OdooEmployeeId").Value;
                    if (odooIdVal != null)
                        emp.OdooEmployeeId = odooIdVal.ToString() ?? "";
                }
                catch
                {
                    // UDF may not exist yet
                }

                employees.Add(emp);
                recordset.MoveNext();
            }

            Marshal.ReleaseComObject(recordset);

            _logger.LogInformation("Listed {Count} active Sales Employees from SAP OSLP", employees.Count);

            return employees;
        }
        finally
        {
            _lock.Release();
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