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

            // Cash vs bank/transfer account
            if (request.IsCashPayment)
            {
                if (!string.IsNullOrEmpty(request.BankOrCashAccountCode))
                    payment.CashAccount = request.BankOrCashAccountCode;

                payment.CashSum = request.PaymentTotal;

                _logger.LogInformation(
                    "Cash payment — CashAccount={CashAccount}, CashSum={CashSum}",
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
                    "Bank/transfer payment — TransferAccount={TransferAccount}, TransferSum={TransferSum}, " +
                    "ForexAccountCode={ForexAccountCode}",
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
                "✅ Incoming Payment created successfully — DocEntry={DocEntry}, DocNum={DocNum}, " +
                "ExternalPaymentId={ExternalPaymentId}, CustomerCode={CustomerCode}, " +
                "PaymentTotal={PaymentTotal}, LineCount={LineCount}",
                docEntry,
                docNum,
                request.ExternalPaymentId,
                request.CustomerCode,
                request.PaymentTotal,
                request.Lines.Count);

            return new SapIncomingPaymentResponse
            {
                DocEntry = docEntry,
                DocNum = docNum,
                ExternalPaymentId = request.ExternalPaymentId,
                OdooPaymentId = request.OdooPaymentId,
                TotalApplied = request.Lines.Sum(l => l.AppliedAmount)
            };
        }
        finally
        {
            _lock.Release();
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