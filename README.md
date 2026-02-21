# SAP-Odoo Middleware

ASP.NET Core Web API middleware that integrates **Odoo** and **SAP Business One** via the SAP DI API. It handles two integration flows:

| Flow | Direction | Endpoint |
|------|-----------|----------|
| Sales Order creation | Odoo → SAP B1 | `POST /api/sales-orders` |
| Delivery confirmation | SAP B1 → Odoo | `POST /api/deliveries` |

## Prerequisites

- **.NET 8 SDK** (or later)
- **SAP Business One DI API** installed on the Windows host (COM interop — `SAPbobsCOM.Company`)
- **Cloudflare Tunnel** exposing the SQL Server to the middleware (already configured)
- **Odoo** instance with JSON-RPC access enabled

## Configuration

Edit `appsettings.json` (or use environment variables / user secrets):

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

Click **Authorize** in Swagger UI, enter your API key, and it will be sent as the `X-Api-Key` header on every request.

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
