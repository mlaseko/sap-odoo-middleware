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
            var lineCaptures = new List<(int lineNum, double qty)>();
            for (int i = 0; i < request.Lines.Count; i++)
            {
                order.Lines.SetCurrentLine(i);
                lineCaptures.Add((order.Lines.LineNum, order.Lines.Quantity));
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
                    var pickList = (PickLists)_company!.GetBusinessObject(BoObjectTypes.oPickLists);
                    pickList.PickDate = DateTime.Now;

                    for (int i = 0; i < lineCaptures.Count; i++)
                    {
                        if (i > 0) pickList.Lines.Add();
                        pickList.Lines.BaseObjectType = ((int)BoObjectTypes.oOrders).ToString();
                        pickList.Lines.OrderEntry = docEntry;
                        pickList.Lines.OrderRowID = lineCaptures[i].lineNum;
                        pickList.Lines.ReleasedQuantity = lineCaptures[i].qty;
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

            // Guard: document must be open for updates
            if (order.DocumentStatus != BoStatus.bost_Open)
            {
                int closedDocNum = order.DocNum;
                Marshal.ReleaseComObject(order);
                throw new InvalidOperationException(
                    $"SAP B1 Sales Order DocEntry={docEntry} (DocNum={closedDocNum}) is closed. " +
                    "Cannot update a closed document — open it in SAP B1 first.");
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
                return CreateInvoiceCopyFromDelivery(invoice, request);
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

        // Copy lines from the delivery document
        // Each invoice line references its source delivery line via BaseType/BaseEntry/BaseLine
        int deliveryLineCount = delivery.Lines.Count;

        _logger.LogInformation(
            "Copying {DeliveryLineCount} line(s) from Delivery DocEntry={DeliveryDocEntry} to Invoice",
            deliveryLineCount, deliveryDocEntry);

        for (int i = 0; i < deliveryLineCount; i++)
        {
            delivery.Lines.SetCurrentLine(i);

            if (i > 0)
                invoice.Lines.Add();

            // Set base document reference (copy-from link)
            invoice.Lines.BaseType = (int)BoObjectTypes.oDeliveryNotes;
            invoice.Lines.BaseEntry = deliveryDocEntry;
            invoice.Lines.BaseLine = delivery.Lines.LineNum;

            _logger.LogDebug(
                "Invoice Line[{Index}]: BaseType=oDeliveryNotes, BaseEntry={BaseEntry}, BaseLine={BaseLine}, " +
                "ItemCode={ItemCode}, Qty={Qty}",
                i,
                deliveryDocEntry,
                delivery.Lines.LineNum,
                delivery.Lines.ItemCode,
                delivery.Lines.Quantity);
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

            // If a base delivery reference is provided at line level, set it
            if (line.BaseDeliveryDocEntry.HasValue && line.BaseDeliveryLineNum.HasValue)
            {
                invoice.Lines.BaseType = (int)BoObjectTypes.oDeliveryNotes;
                invoice.Lines.BaseEntry = line.BaseDeliveryDocEntry.Value;
                invoice.Lines.BaseLine = line.BaseDeliveryLineNum.Value;

                _logger.LogDebug(
                    "Manual Invoice Line[{Index}]: BaseEntry={BaseEntry}, BaseLine={BaseLine}",
                    i, line.BaseDeliveryDocEntry.Value, line.BaseDeliveryLineNum.Value);
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

            // Guard: document must be open for updates
            if (invoice.DocumentStatus != BoStatus.bost_Open)
            {
                int closedDocNum = invoice.DocNum;
                Marshal.ReleaseComObject(invoice);
                throw new InvalidOperationException(
                    $"SAP B1 AR Invoice DocEntry={docEntry} (DocNum={closedDocNum}) is closed. " +
                    "Cannot update a closed document — open it in SAP B1 first.");
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

            // ── Enforce Copy-To: every line must reference the base invoice ──
            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                if (!line.BaseInvoiceDocEntry.HasValue || !line.BaseInvoiceLineNum.HasValue)
                {
                    throw new InvalidOperationException(
                        $"Credit Memo line[{i}] (ItemCode={line.ItemCode}) is missing " +
                        "BaseInvoiceDocEntry/BaseInvoiceLineNum. Credit memos must be " +
                        "created by copying from the original AR Invoice (Copy-To).");
                }
            }

            _logger.LogInformation(
                "Creating AR Credit Memo — ExternalCreditMemoId={ExternalCreditMemoId}, " +
                "CustomerCode={CustomerCode}, SapBaseInvoiceDocEntry={SapBaseInvoiceDocEntry}, " +
                "SapBaseDeliveryDocEntry={SapBaseDeliveryDocEntry}, LineCount={LineCount}",
                request.ExternalCreditMemoId,
                request.CustomerCode,
                request.SapBaseInvoiceDocEntry,
                request.SapBaseDeliveryDocEntry,
                request.Lines.Count);

            // ── Pre-validate: ensure base invoice(s) are open ──
            var baseInvoiceDocEntries = request.Lines
                .Select(l => l.BaseInvoiceDocEntry!.Value)
                .Distinct()
                .ToList();

            foreach (var baseDocEntry in baseInvoiceDocEntries)
            {
                var invoice = (Documents)_company!.GetBusinessObject(BoObjectTypes.oInvoices);
                try
                {
                    if (invoice.GetByKey(baseDocEntry))
                    {
                        if (invoice.DocumentStatus != BoStatus.bost_Open)
                        {
                            int closedDocNum = invoice.DocNum;
                            throw new InvalidOperationException(
                                $"SAP B1 AR Invoice DocEntry={baseDocEntry} (DocNum={closedDocNum}) " +
                                "is closed. Cannot create a Credit Memo against a closed invoice — " +
                                "open the invoice in SAP B1 first.");
                        }
                    }
                    // If invoice not found, let SAP DI API handle the error naturally
                }
                finally
                {
                    Marshal.ReleaseComObject(invoice);
                }
            }

            var creditMemo = (Documents)_company!.GetBusinessObject(BoObjectTypes.oCreditNotes);

            try
            {
                PopulateCreditMemo(creditMemo, request);

                int result = creditMemo.Add();

                if (result != 0)
                {
                    _company!.GetLastError(out int errCode, out string errMsg);
                    Marshal.ReleaseComObject(creditMemo);

                    _logger.LogError(
                        "Failed to create AR Credit Memo for {ExternalCreditMemoId}: DI API error {ErrCode}: {ErrMsg}",
                        request.ExternalCreditMemoId, errCode, errMsg);

                    throw new InvalidOperationException(
                        $"SAP DI API error {errCode}: {errMsg}");
                }

                int docEntry = int.Parse(_company!.GetNewObjectKey());
                creditMemo.GetByKey(docEntry);
                int docNum = creditMemo.DocNum;

                Marshal.ReleaseComObject(creditMemo);

                _logger.LogInformation(
                    "AR Credit Memo created (Copy-To): DocEntry={DocEntry}, DocNum={DocNum}, " +
                    "ExternalCreditMemoId={ExternalCreditMemoId}, LineCount={LineCount}",
                    docEntry, docNum, request.ExternalCreditMemoId, request.Lines.Count);

                return new SapCreditMemoResponse
                {
                    DocEntry = docEntry,
                    DocNum = docNum,
                    ExternalCreditMemoId = request.ExternalCreditMemoId,
                    OdooInvoiceId = request.OdooInvoiceId
                };
            }
            catch
            {
                Marshal.ReleaseComObject(creditMemo);
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Populates the credit memo DI API object with header, UDFs, and lines.
    /// Every line is created by Copy-To from the original AR Invoice (BaseType=13).
    /// The base invoice must be open (validated by CreateCreditMemoAsync before this call).
    /// </summary>
    private void PopulateCreditMemo(Documents creditMemo, SapCreditMemoRequest request)
    {
        // Header fields
        creditMemo.CardCode = request.CustomerCode;
        creditMemo.NumAtCard = request.ExternalCreditMemoId;

        if (request.DocDate.HasValue)
            creditMemo.DocDate = request.DocDate.Value;

        if (request.DueDate.HasValue)
            creditMemo.DocDueDate = request.DueDate.Value;

        if (!string.IsNullOrEmpty(request.Currency))
            creditMemo.DocCurrency = request.Currency;

        // UDFs
        TrySetUserField(creditMemo.UserFields, "U_Odoo_Invoice_ID", request.ExternalCreditMemoId, "Credit Memo header");

        if (!string.IsNullOrEmpty(request.UOdooSoId))
            TrySetUserField(creditMemo.UserFields, "U_Odoo_SO_ID", request.UOdooSoId, "Credit Memo header");

        var syncDate = DateTime.UtcNow.Date;
        TrySetUserField(creditMemo.UserFields, "U_Odoo_LastSync", syncDate, "Credit Memo header");
        TrySetUserField(creditMemo.UserFields, "U_Odoo_SyncDir", SyncDirectionOdooToSap, "Credit Memo header");

        // Lines — always Copy-To from original invoice
        for (int i = 0; i < request.Lines.Count; i++)
        {
            if (i > 0)
                creditMemo.Lines.Add();

            var line = request.Lines[i];

            creditMemo.Lines.ItemCode = line.ItemCode;
            creditMemo.Lines.Quantity = line.Quantity;
            creditMemo.Lines.UnitPrice = line.Price;

            if (line.DiscountPercent.HasValue)
                creditMemo.Lines.DiscountPercent = line.DiscountPercent.Value;

            // Warehouse: only set when the caller supplies an explicit value.
            // SAP auto-resolves each item's default warehouse from the Item Master,
            // which handles both inventory and service-type items correctly.
            if (!string.IsNullOrEmpty(line.WarehouseCode))
                creditMemo.Lines.WarehouseCode = line.WarehouseCode;

            // Copy-To from AR Invoice (BaseType=13) — mandatory
            creditMemo.Lines.BaseType = (int)BoObjectTypes.oInvoices;  // 13
            creditMemo.Lines.BaseEntry = line.BaseInvoiceDocEntry!.Value;
            creditMemo.Lines.BaseLine = line.BaseInvoiceLineNum!.Value;

            _logger.LogDebug(
                "Credit Memo Line[{Index}]: BaseType=oInvoices, BaseEntry={BaseEntry}, BaseLine={BaseLine}",
                i, line.BaseInvoiceDocEntry.Value, line.BaseInvoiceLineNum.Value);

            // ActualBaseEntry/ActualBaseLine for delivery chain (SO → ODLN → OINV)
            if (line.BaseDeliveryDocEntry.HasValue && line.BaseDeliveryLineNum.HasValue)
            {
                creditMemo.Lines.ActualBaseEntry = line.BaseDeliveryDocEntry.Value;
                creditMemo.Lines.ActualBaseLine = line.BaseDeliveryLineNum.Value;

                _logger.LogDebug(
                    "Credit Memo Line[{Index}]: ActualBaseEntry={ActualBaseEntry}, ActualBaseLine={ActualBaseLine}",
                    i, line.BaseDeliveryDocEntry.Value, line.BaseDeliveryLineNum.Value);
            }

            _logger.LogDebug(
                "Credit Memo Line[{Index}]: ItemCode={ItemCode}, Qty={Qty}, Price={Price}",
                i, line.ItemCode, line.Quantity, line.Price);
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

            // Guard: document must be open for updates
            if (creditMemo.DocumentStatus != BoStatus.bost_Open)
            {
                int closedDocNum = creditMemo.DocNum;
                Marshal.ReleaseComObject(creditMemo);
                throw new InvalidOperationException(
                    $"SAP B1 AR Credit Memo DocEntry={docEntry} (DocNum={closedDocNum}) is closed. " +
                    "Cannot update a closed document — open it in SAP B1 first.");
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

            // ── Validate: invoice DocEntry is required for Return Request ──
            if (!request.SapBaseInvoiceDocEntry.HasValue || request.SapBaseInvoiceDocEntry.Value <= 0)
            {
                throw new InvalidOperationException(
                    "SapBaseInvoiceDocEntry is required. Return Requests are created " +
                    "by Copy-To from the A/R Invoice (BaseType=13).");
            }

            _logger.LogInformation(
                "Creating Return Request (ORRR) — ExternalReturnId={ExternalReturnId}, " +
                "CustomerCode={CustomerCode}, SapBaseInvoiceDocEntry={SapBaseInvoiceDocEntry}, LineCount={LineCount}",
                request.ExternalReturnId,
                request.CustomerCode,
                request.SapBaseInvoiceDocEntry,
                request.Lines.Count);

            // ── Pre-validate: ensure the A/R Invoice is open ──
            var baseInvoice = (Documents)_company!.GetBusinessObject(BoObjectTypes.oInvoices);
            var invoiceLineIndex = new Dictionary<string, int>();  // ItemCode → LineNum
            try
            {
                if (!baseInvoice.GetByKey(request.SapBaseInvoiceDocEntry.Value))
                {
                    throw new InvalidOperationException(
                        $"SAP B1 A/R Invoice DocEntry={request.SapBaseInvoiceDocEntry.Value} not found.");
                }

                if (baseInvoice.DocumentStatus != BoStatus.bost_Open)
                {
                    int closedDocNum = baseInvoice.DocNum;
                    throw new InvalidOperationException(
                        $"SAP B1 A/R Invoice DocEntry={request.SapBaseInvoiceDocEntry.Value} " +
                        $"(DocNum={closedDocNum}) is closed. Cannot create a Return Request " +
                        "when the invoice is closed — reverse the incoming payment " +
                        "in SAP B1 first to re-open the invoice.");
                }

                // Build ItemCode → LineNum index from the invoice for Copy-To mapping
                for (int ln = 0; ln < baseInvoice.Lines.Count; ln++)
                {
                    baseInvoice.Lines.SetCurrentLine(ln);
                    string itemCode = baseInvoice.Lines.ItemCode;
                    // Use first occurrence if item appears on multiple lines
                    if (!invoiceLineIndex.ContainsKey(itemCode))
                    {
                        invoiceLineIndex[itemCode] = baseInvoice.Lines.LineNum;
                    }
                }

                _logger.LogInformation(
                    "A/R Invoice DocEntry={DocEntry} is open with {LineCount} lines",
                    request.SapBaseInvoiceDocEntry.Value, invoiceLineIndex.Count);
            }
            finally
            {
                Marshal.ReleaseComObject(baseInvoice);
            }

            // ── Create Return Request (ORRR) with Copy-To from Invoice ──
            var returnRequest = (Documents)_company!.GetBusinessObject(BoObjectTypes.oReturnRequest);

            SetGoodsReturnHeader(returnRequest, request);

            for (int i = 0; i < request.Lines.Count; i++)
            {
                if (i > 0)
                    returnRequest.Lines.Add();

                var line = request.Lines[i];

                returnRequest.Lines.ItemCode = line.ItemCode;
                returnRequest.Lines.Quantity = line.Quantity;

                // Copy-To from A/R Invoice (BaseType=13)
                returnRequest.Lines.BaseType = (int)BoObjectTypes.oInvoices;  // 13
                returnRequest.Lines.BaseEntry = request.SapBaseInvoiceDocEntry.Value;

                // Resolve the invoice line number by matching ItemCode
                if (invoiceLineIndex.TryGetValue(line.ItemCode, out int invoiceLineNum))
                {
                    returnRequest.Lines.BaseLine = invoiceLineNum;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Return Request line[{i}] ItemCode={line.ItemCode} not found on " +
                        $"A/R Invoice DocEntry={request.SapBaseInvoiceDocEntry.Value}. " +
                        "Cannot create Copy-To reference.");
                }

                _logger.LogDebug(
                    "Return Request Line[{Index}]: ItemCode={ItemCode}, Qty={Qty}, " +
                    "BaseType=oInvoices, BaseEntry={BaseEntry}, BaseLine={BaseLine}",
                    i, line.ItemCode, line.Quantity,
                    request.SapBaseInvoiceDocEntry.Value, invoiceLineNum);
            }

            int result = returnRequest.Add();

            if (result != 0)
            {
                _company!.GetLastError(out int errCode, out string errMsg);
                Marshal.ReleaseComObject(returnRequest);

                _logger.LogError(
                    "Failed to create Return Request for {ExternalReturnId}: DI API error {ErrCode}: {ErrMsg}",
                    request.ExternalReturnId, errCode, errMsg);

                throw new InvalidOperationException(
                    $"SAP DI API error {errCode}: {errMsg}");
            }

            int docEntry = int.Parse(_company!.GetNewObjectKey());

            returnRequest.GetByKey(docEntry);
            int docNum = returnRequest.DocNum;

            Marshal.ReleaseComObject(returnRequest);

            _logger.LogInformation(
                "✅ Return Request created: DocEntry={DocEntry}, DocNum={DocNum}, " +
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

            // Guard: document must be open for updates
            if (goodsReturn.DocumentStatus != BoStatus.bost_Open)
            {
                int closedDocNum = goodsReturn.DocNum;
                Marshal.ReleaseComObject(goodsReturn);
                throw new InvalidOperationException(
                    $"SAP B1 Goods Return DocEntry={docEntry} (DocNum={closedDocNum}) is closed. " +
                    "Cannot update a closed document — open it in SAP B1 first.");
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