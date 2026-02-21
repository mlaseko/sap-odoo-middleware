# SAP-Odoo Middleware

ASP.NET Core Web API middleware that integrates **Odoo** and **SAP Business One** via the SAP DI API. It handles two integration flows:

| Flow | Direction | Endpoint |
|------|-----------|----------|
| SAP B1 connectivity check | Middleware → SAP B1 | `GET /api/sapb1/ping` |
| Sales Order creation | Odoo → SAP B1 | `POST /api/sales-orders` |
| Delivery confirmation | SAP B1 → Odoo | `POST /api/deliveries` |

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
   | `dst_MSSQL2019` | SQL Server 2019 (default) |
   | `dst_MSSQL2017` | SQL Server 2017 |
   | `dst_MSSQL2016` | SQL Server 2016 |
   | `dst_MSSQL2014` | SQL Server 2014 |
   | `dst_MSSQL2012` | SQL Server 2012 |
   | `dst_HANADB` | SAP HANA |

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

### Create Sales Order (Odoo → SAP B1)

```
POST /api/sales-orders
X-Api-Key: YOUR_KEY
Content-Type: application/json

{
  "odoo_so_ref": "SO0042",
  "card_code": "C10000",
  "doc_date": "2025-01-15",
  "doc_due_date": "2025-01-30",
  "lines": [
    {
      "item_code": "ITEM001",
      "quantity": 10,
      "unit_price": 25.50,
      "warehouse_code": "01",
      "odoo_line_ref": "SOL/0042/1"
    }
  ]
}
```

**Response:**

```json
{
  "success": true,
  "data": {
    "doc_entry": 100,
    "doc_num": 200,
    "odoo_so_ref": "SO0042",
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
  "odoo_so_ref": "SO0042",
  "sap_delivery_no": "DN-001",
  "delivery_date": "2025-01-20",
  "status": "delivered"
}
```

**Response:**

```json
{
  "success": true,
  "data": {
    "odoo_so_ref": "SO0042",
    "picking_id": 77,
    "picking_name": "WH/OUT/00012",
    "state": "done",
    "sap_delivery_no": "DN-001"
  }
}
```

## Process Flows

### SO Integration (Odoo → SAP B1)

1. Sales Order created in Odoo
2. Odoo sends POST to `/api/sales-orders` with order data
3. Middleware creates SO in SAP B1 via DI API (with Odoo ref on header `NumAtCard`)
4. Middleware auto-creates a Pick List for the new SO
5. Warehouse completes picking/packing in SAP B1
6. SAP posts Delivery Note (stock is issued)

### Delivery Update (SAP → Odoo)

1. SAP Delivery Note is posted
2. SAP sends header-only POST to `/api/deliveries`
3. Middleware finds the `sale.order` in Odoo by `odoo_so_ref`
4. Finds the related outgoing `stock.picking` (not done/cancelled)
5. Runs standard Odoo workflow:
   - `action_assign()` — reserve stock
   - `action_set_quantities_to_reservation()` — set qty_done = demand
   - `button_validate()` — with context to skip backorder/immediate-transfer wizards
6. Writes SAP delivery number/date onto the picking

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
