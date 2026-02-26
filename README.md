# SAP-Odoo Middleware

ASP.NET Core Web API middleware that integrates **Odoo** and **SAP Business One** via the SAP DI API. It handles the following integration flows:

| Flow | Direction | Endpoint |
|------|-----------|----------|
| SAP B1 connectivity check | Middleware → SAP B1 | `GET /api/sapb1/ping` |
| Odoo connectivity check | Middleware → Odoo | `GET /api/odoo/ping` |
| Sales Order creation | Odoo → SAP B1 | `POST /api/sales-orders` |
| AR Invoice creation | Odoo → SAP B1 | `POST /api/invoices` |
| Incoming Payment creation | Odoo → SAP B1 | `POST /api/incoming-payments` |
| Delivery confirmation | SAP B1 → Odoo | `POST /api/deliveries` |
| COGS Journal Entry | Middleware → Odoo | `POST /api/cogs-journals` |

## Prerequisites

- **.NET 8 SDK** (or later)
- **SAP Business One DI API** installed on the Windows host (COM interop — `SAPbobsCOM.Company`)
  - The DI API version **must exactly match** the SAP B1 server's Feature Package / patch level.
    For example, if the server runs **10.00.110**, install the **10.00.110** DI API on the app machine.
    A mismatch is the most common cause of connection error **-132** (SBO user authentication failure).
  - The DI API bitness must match the application's target platform:
    **x86 app → 32-bit DI API**, **x64 app → 64-bit DI API**.
- **Cloudflare Tunnel** exposing the SQL Server to the middleware (already configured)
- **Odoo** instance with JSON-RPC access enabled

## Configuration

Edit `appsettings.json` (or use environment variables / user secrets):

> **Tip:** Any setting can be overridden via an environment variable using the double-underscore separator, e.g. `SapB1__SLDServer=WIN-GJGQ73V0C3K:40000`.

```jsonc
{
  "ApiKey": {
    "Key": "YOUR_SECURE_API_KEY"
  },
  "SapB1": {
    "Server": "sql-server-host",
    "CompanyDb": "SBODemoUS",
    "UserName": "manager",
    "Password": "secret",
    "DbServerType": "dst_MSSQL2019",
    "LicenseServer": "license-host:30000",
    "SLDServer": "WIN-GJGQ73V0C3K:40000",
    "AutoCreatePickList": true
  },
  "Odoo": {
    "BaseUrl": "https://mycompany.odoo.com",
    "Database": "mycompany",
    "UserName": "admin@mycompany.com",
    "Password": "secret"
  }
}
```

## Build & Run

```bash
dotnet build
dotnet run --project src/SapOdooMiddleware
```

The API starts on `http://localhost:5000` by default.

## Troubleshooting: SAP B1 DI API Connection

Use `GET /api/sapb1/ping` (authenticated) to test connectivity. Common failures and their resolutions:

### Error -132 — SBO user authentication failure

This is the most frequent connection error. Work through this checklist in order:

1. **DI API version matches the server patch level (critical)**
   Install the DI API that matches your SAP B1 server exactly, e.g. **10.00.110** client DI API for a
   **10.00.110** server. Even a minor Feature-Package mismatch (e.g. 10.00.170 DI API against a
   10.00.110 server) triggers -132. Reinstall the DI API from the SAP B1 installation media that
   shipped with your server version.

2. **LicenseServer is set and reachable**
   `LicenseServer` is always required. The default SAP B1 license-server port is **30000**:
   ```
   "LicenseServer": "YOUR_SAP_SERVER_HOST:30000"
   ```
   If this setting is empty the middleware logs a warning at startup and the DI API will reject the
   connection. Verify the license service (`SAPBOLicenseServer`) is running on the SAP B1 host.

3. **UserName / Password are SAP B1 application credentials**
   Use an SAP B1 application-level user (e.g. `manager`), **not** a SQL Server login. The DI API
   authenticates against SAP B1, not against SQL Server directly.

4. **DI API bitness matches the application target platform**
   - **x86 application → 32-bit DI API** (default for most SAP B1 DI API installers)
   - **x64 application → 64-bit DI API** (check SAP Note 1492196 for availability)

5. **DbServerType matches your SQL Server version**
   Supported values and their SQL Server versions:

   | `DbServerType` value | SQL Server version |
   |---|---|
   | `dst_MSSQL2019` (or `MSSQL2019`) | SQL Server 2019 (default) |
   | `dst_MSSQL2017` (or `MSSQL2017`) | SQL Server 2017 |
   | `dst_MSSQL2016` (or `MSSQL2016`) | SQL Server 2016 |
   | `dst_MSSQL2014` (or `MSSQL2014`) | SQL Server 2014 |
   | `dst_MSSQL2012` (or `MSSQL2012`) | SQL Server 2012 |
   | `dst_HANADB` (or `HANADB`) | SAP HANA |

   The `dst_` prefix is optional and matching is case-insensitive.

   > **Automatic fallback for error -119:** Different SAPbobsCOM versions assign
   > different internal enum ordinals to the same `DbServerType` value. If the
   > DI API rejects the first ordinal with error **-119** ("Database server type
   > not supported"), the middleware automatically retries with an alternative
   > ordinal. No configuration change is needed — just set the logical type that
   > matches your SQL Server version.

6. **SLDServer (if required by your landscape)**
   Needed when SAP B1 is registered with an SLD (System Landscape Directory). Format:
   ```
   "SLDServer": "YOUR_SLD_HOST:40000"
   ```
   Leave empty if SLD is not configured.

### SAPbobsCOM.Company COM class not found

The DI API is not installed or was not registered. Re-run the DI API installer and verify that
`SAPbobsCOM.Company` can be instantiated from a 32-bit or 64-bit COM host matching your application.

## Swagger UI

Swagger UI is enabled automatically when running in the **Development** environment (the default for `dotnet run`).

Open your browser at:

```
http://localhost:5000/swagger
```

To enable Swagger UI in Production (e.g. when accessed via the Cloudflare hostname), set the environment variable before starting the service:

```bash
ENABLE_SWAGGER=true dotnet run --project src/SapOdooMiddleware
```

Then open it via the Cloudflare hostname, e.g.:

```
https://<your-cloudflare-hostname>/swagger
```

### Authenticating in Swagger UI

1. Click the **Authorize 🔒** button at the top of the Swagger UI page.
2. In the dialog that appears, enter your API key in the **Value** field.
3. Click **Authorize**, then **Close**.

Swagger UI will now send the `X-Api-Key` header automatically on every **Try it out** request.

## Authentication

All endpoints (except `/health`) require the `X-Api-Key` header:

```
X-Api-Key: YOUR_SECURE_API_KEY
```

## API Endpoints

### Health Check

```
GET /health
```

No authentication required. Returns `{ "status": "healthy", "timestamp": "..." }`.

### SAP B1 Connectivity Check

```
GET /api/sapb1/ping
X-Api-Key: YOUR_KEY
```

Verifies connectivity to the SAP B1 DI API and returns non-secret connection details.

**Response (200 — connected):**

```json
{
  "success": true,
  "data": {
    "connected": true,
    "server": "sql-server-host",
    "company_db": "SBODemoUS",
    "license_server": "license-host:30000",
    "sld_server": "WIN-GJGQ73V0C3K:40000",
    "company_name": "Demo Company",
    "version": "10.0"
  }
}
```

**Response (500 — connection failed):**

```json
{
  "success": false,
  "errors": ["SAP B1 DI API connection failed (65): ..."]
}
```

### Odoo Connectivity Check

```
GET /api/odoo/ping
X-Api-Key: YOUR_KEY
```

Verifies connectivity to the Odoo JSON-RPC API by authenticating and returning session information. Does not modify any data.

**Response (200 — connected):**

```json
{
  "success": true,
  "data": {
    "connected": true,
    "uid": 2,
    "database": "mlaseko-molas-lubes",
    "server_version": "18.0",
    "base_url": "https://mlaseko-molas-lubes.odoo.com",
    "user_name": "admin@company.com"
  }
}
```

**Response (500 — connection failed):**

```json
{
  "success": false,
  "errors": ["Odoo authentication failed — uid is null."]
}
```

### Create Sales Order (Odoo → SAP B1)

```
POST /api/sales-orders
X-Api-Key: YOUR_KEY
Content-Type: application/json

{
  "u_odoo_so_id": "SO0042",
  "card_code": "C10000",
  "doc_date": "2025-01-15",
  "doc_due_date": "2025-01-30",
  "lines": [
    {
      "item_code": "ITEM001",
      "quantity": 10,
      "unit_price": 25.50,
      "gross_buy_pr": 20.00,
      "warehouse_code": "01",
      "u_odoo_so_line_id": "SOL/0042/1",
      "u_odoo_move_id": "MOVE/001",
      "u_odoo_delivery_id": "PICK/001"
    }
  ]
}
```

**Field mapping (SAP B1):**

| JSON field | SAP B1 field / UDF | Required |
|---|---|---|
| `u_odoo_so_id` | `NumAtCard` + header UDF `U_Odoo_SO_ID` | ✅ |
| `card_code` | `CardCode` | ✅ |
| `doc_date` | `DocDate` | No |
| `doc_due_date` | `DocDueDate` | No |
| `lines[].item_code` | `Lines.ItemCode` | ✅ |
| `lines[].quantity` | `Lines.Quantity` | ✅ |
| `lines[].unit_price` | `Lines.UnitPrice` | ✅ |
| `lines[].gross_buy_pr` | `Lines.GrossBuyPr` | No |
| `lines[].warehouse_code` | `Lines.WarehouseCode` | No |
| `lines[].u_odoo_so_line_id` | Line UDF `U_Odoo_SOLine_ID` | No |
| `lines[].u_odoo_move_id` | Line UDF `U_Odoo_Move_ID` | No |
| `lines[].u_odoo_delivery_id` | Line UDF `U_Odoo_Delivery_ID` | No |

> **Note:** UDF fields (`U_Odoo_SO_ID`, `U_Odoo_SOLine_ID`, `U_Odoo_Move_ID`, `U_Odoo_Delivery_ID`)
> must be defined in your SAP B1 system (RDR and RDR1 tables respectively). If a UDF is not present,
> the middleware logs a warning and continues — it will **not** abort the order creation.

> **Backwards compatibility:** The deprecated field `odoo_so_ref` is still accepted as an alias for
> `u_odoo_so_id`. When both are supplied, `u_odoo_so_id` takes precedence.
> `odoo_so_ref` will be removed in a future version.

**Response:**

```json
{
  "success": true,
  "data": {
    "doc_entry": 100,
    "doc_num": 200,
    "u_odoo_so_id": "SO0042",
    "pick_list_entry": 50
  }
}
```

### Confirm Delivery (SAP B1 → Odoo)

```
POST /api/deliveries
X-Api-Key: YOUR_KEY
Content-Type: application/json

{
  "u_odoo_so_id": "SO0042",
  "sap_delivery_no": "DN-001",
  "delivery_date": "2025-01-20",
  "status": "delivered"
}
```

> **Backwards compatibility:** The deprecated field `odoo_so_ref` is still accepted as an alias for
> `u_odoo_so_id`. When both are supplied, `u_odoo_so_id` takes precedence.
> `odoo_so_ref` will be removed in a future version.

**Response:**

```json
{
  "success": true,
  "data": {
    "u_odoo_so_id": "SO0042",
    "picking_id": 77,
    "picking_name": "WH/OUT/00012",
    "state": "done",
    "sap_delivery_no": "DN-001"
  }
}
```

### Create Incoming Payment (Odoo → SAP B1)

```
POST /api/incoming-payments
X-Api-Key: YOUR_KEY
Content-Type: application/json

{
  "external_payment_id": "BNK1/2026/00001",
  "customer_code": "C10000",
  "doc_date": "2026-02-25",
  "currency": "TZS",
  "payment_total": 500000.0,
  "is_partial": false,
  "journal_code": "NMB TZS",
  "bank_or_cash_account_code": "1026217",
  "is_cash_payment": false,
  "odoo_payment_id": 55,
  "lines": [
    {
      "sap_invoice_doc_entry": 700,
      "applied_amount": 500000.0,
      "discount_amount": null,
      "odoo_invoice_id": 42
    }
  ]
}
```

**Field mapping (SAP B1):**

| JSON field | SAP B1 field | Required |
|---|---|---|
| `external_payment_id` | `CounterReference` | ✅ |
| `customer_code` | `CardCode` | ✅ |
| `doc_date` | `DocDate` | No |
| `currency` | `DocCurrency` | No |
| `payment_total` | `CashSum` or `TransferSum` (see `is_cash_payment`) | Yes |
| `is_cash_payment` | Determines Cash vs Bank posting | Yes |
| `bank_or_cash_account_code` | `CashAccount` or `TransferAccount` | No |
| `forex_account_code` | `TransferAccount` (cross-currency) | No |
| `odoo_payment_id` | *(write-back only)* Odoo `account.payment` ID | No |
| `lines[].sap_invoice_doc_entry` | `Invoices.DocEntry` (RCT2) | ✅ |
| `lines[].applied_amount` | `Invoices.SumApplied` | Yes |
| `lines[].discount_amount` | Converted to `Invoices.DiscountPercent` | No |

> When `odoo_payment_id` is provided, the middleware writes `x_sap_incoming_payment_docentry`
> and `x_sap_incoming_payment_docnum` back to the Odoo `account.payment` record after
> the SAP Incoming Payment is created successfully.

**Response:**

```json
{
  "success": true,
  "data": {
    "doc_entry": 1001,
    "doc_num": 2001,
    "odoo_payment_id": 55,
    "odoo_write_back_success": true,
    "odoo_write_back_error": null
  }
}
```

## Process Flows

### SO Integration (Odoo → SAP B1)

1. Sales Order created in Odoo
2. Odoo sends POST to `/api/sales-orders` with order data
3. Middleware creates SO in SAP B1 via DI API (with Odoo identifier on header `NumAtCard` and UDF `U_Odoo_SO_ID`)
4. Middleware auto-creates a Pick List for the new SO
5. Warehouse completes picking/packing in SAP B1
6. SAP posts Delivery Note (stock is issued)

### Delivery Update (SAP → Odoo)

1. SAP Delivery Note is posted
2. SAP sends header-only POST to `/api/deliveries`
3. Middleware finds the `sale.order` in Odoo by `u_odoo_so_id` (matched against the Odoo `name` field, e.g. "SO0042")
4. Finds the related outgoing `stock.picking` (not done/cancelled)
5. Runs standard Odoo workflow:
   - `action_assign()` — reserve stock
   - `action_set_quantities_to_reservation()` — set qty_done = demand
   - `button_validate()` — with context to skip backorder/immediate-transfer wizards
6. Writes SAP delivery number/date onto the picking

### Incoming Payment (Odoo → SAP B1)

1. Payment is confirmed/posted in Odoo
2. An Odoo **Automated Action** (base.automation) triggers on `account.payment` when the state changes to `posted`
3. The automated action sends a POST request to the middleware's `/api/incoming-payments` endpoint with the payment details
4. Middleware creates an Incoming Payment (ORCT) in SAP B1 via DI API
5. Middleware writes SAP DocEntry and DocNum back to the Odoo `account.payment` record (fields `x_sap_incoming_payment_docentry` and `x_sap_incoming_payment_docnum`)

#### Odoo Automated Action Setup

To trigger incoming payment sync, create an **Automated Action** in Odoo:

1. Go to **Settings → Technical → Automation → Automated Actions**
2. Create a new automated action with:
   - **Model**: `account.payment` (`account.payment`)
   - **Trigger**: On Update
   - **When Updated**: `state` (set to `posted`)
   - **Action**: Execute Python Code
3. The Python code should build the payment payload and POST it to the middleware's `/api/incoming-payments` endpoint

Example Python code for the automated action:

```python
import json
import requests

for payment in records:
    if payment.state != 'posted' or payment.payment_type != 'inbound':
        continue

    # Build invoice allocations from reconciled move lines
    lines = []
    for partial in payment.move_id.line_ids.matched_debit_ids:
        invoice_move = partial.debit_move_id.move_id
        sap_docentry = invoice_move.x_sap_invoice_docentry
        if sap_docentry:
            lines.append({
                "sap_invoice_doc_entry": sap_docentry,
                "applied_amount": partial.amount,
                "odoo_invoice_id": invoice_move.id,
            })

    if not lines:
        continue

    payload = {
        "external_payment_id": payment.name,
        "customer_code": payment.partner_id.x_sap_card_code or payment.partner_id.ref or "",
        "doc_date": str(payment.date),
        "currency": payment.currency_id.name,
        "payment_total": payment.amount,
        "is_partial": payment.amount < sum(l["applied_amount"] for l in lines),
        "journal_code": payment.journal_id.name,
        "bank_or_cash_account_code": payment.journal_id.x_sap_gl_account or "",
        "is_cash_payment": payment.journal_id.type == "cash",
        "odoo_payment_id": payment.id,
        "lines": lines,
    }

    try:
        resp = requests.post(
            "https://your-middleware-host.com/api/incoming-payments",
            json=payload,
            headers={"X-Api-Key": "YOUR_API_KEY", "Content-Type": "application/json"},
            timeout=30,
        )
        resp.raise_for_status()
        log("Payment %s synced to SAP: %s" % (payment.name, resp.text), level="info")
    except Exception as e:
        log("Payment %s sync failed: %s" % (payment.name, e), level="error")
```

> **Important:** Replace `your-middleware-host.com` and `YOUR_API_KEY` with your actual middleware
> hostname and API key. Ensure the Odoo custom fields (`x_sap_card_code`,
> `x_sap_gl_account`, `x_sap_invoice_docentry`, `x_sap_incoming_payment_docentry`,
> `x_sap_incoming_payment_docnum`) are created on the respective Odoo models.

## Tests

```bash
dotnet test
```

## Project Structure

```
├── src/SapOdooMiddleware/
│   ├── Configuration/       # Settings classes (SapB1, Odoo, ApiKey)
│   ├── Controllers/         # API controllers
│   ├── Middleware/           # API key authentication
│   ├── Models/
│   │   ├── Api/             # ApiResponse envelope
│   │   ├── Odoo/            # Delivery DTOs
│   │   └── Sap/             # Sales Order DTOs
│   ├── Services/            # SAP DI API + Odoo JSON-RPC services
│   ├── Program.cs           # App entry point
│   └── appsettings.json     # Configuration
└── tests/SapOdooMiddleware.Tests/
```
