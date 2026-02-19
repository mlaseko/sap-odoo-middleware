# SAP B1 ↔ Odoo 19 Integration Middleware

ASP.NET Core Web API middleware that bridges SAP Business One (on-premises) with Odoo 19, exposed securely via Cloudflare Tunnel.

## Architecture

```
Odoo 19 (Cloud)  →  Cloudflare Edge  →  Tunnel (cloudflared)  →  ASP.NET Core API  →  SAP B1
                     (molas-api.app)     (Zero Trust)             (localhost:5000)      (SQL + Service Layer)
```

**Data Flow:**
- **Read (SAP → Odoo):** API queries SAP SQL Server directly via Dapper
- **Write (Odoo → SAP):** Webhooks → SAP Service Layer REST API
- **Background Sync:** Hangfire jobs detect changes and push to Odoo via JSON-RPC

## Tech Stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 8 / ASP.NET Core |
| SAP Reads | Dapper + Microsoft.Data.SqlClient |
| SAP Writes | SAP B1 Service Layer (REST) |
| Staging DB | Neon (PostgreSQL) + EF Core |
| Background Jobs | Hangfire |
| Authentication | API Key + Cloudflare header validation |
| Network | Cloudflare Tunnel (Zero Trust) |
| Logging | Serilog |

## Quick Start

### Prerequisites
- .NET 8 SDK
- SAP B1 SQL Server access (read-only)
- Neon PostgreSQL database
- Cloudflare account with tunnel configured

### Setup

1. Clone and restore:
```bash
cd custom/addons/sap_odoo_middleware
dotnet restore SapOdooMiddleware.slnx
```

2. Initialize Neon database:
```bash
psql $NEON_CONNECTION_STRING -f infrastructure/scripts/seed-neon.sql
```

3. Configure environment variables:
```bash
export SAP__SqlConnectionString="Server=WIN-GJGQ73V0C3K;Database=Molas_Lubes_LTD;User Id=manager;Password=xxx;TrustServerCertificate=true"
export SAP__ServiceLayerUser="manager"
export SAP__ServiceLayerPassword="xxx"
export NEON__ConnectionString="Host=xxx.neon.tech;Database=middleware;Username=xxx;Password=xxx;SslMode=Require"
export AUTH__ApiKey="your-secure-api-key"
export ODOO__Url="https://your-odoo.com"
export ODOO__Database="your_db"
export ODOO__User="integration@company.com"
export ODOO__Password="xxx"
```

4. Run:
```bash
dotnet run --project src/SapOdooMiddleware
```

5. Access Swagger UI: http://localhost:5000/swagger

## API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/products` | List products with pagination |
| GET | `/api/products/{code}` | Single product with warehouse stock |
| GET | `/api/inventory/stock` | Warehouse stock levels |
| GET | `/api/partners/customers` | Customer list |
| GET | `/api/partners/suppliers` | Supplier list |
| GET | `/api/orders/sales-orders` | Sales orders |
| POST | `/api/orders/sales-order` | Create sales order in SAP |
| POST | `/api/webhooks/delivery-confirmation` | Delivery → SAP |
| POST | `/api/webhooks/invoice-cogs` | Invoice COGS → SAP |
| POST | `/api/sync/trigger` | Manual sync trigger |
| GET | `/api/sync/status` | Sync status dashboard |
| GET | `/api/health/detailed` | Health check with timing |

All endpoints require `X-Api-Key` header (except `/api/health` and `/swagger`).

## Cloudflare Configuration

- **Domain:** molas-api.app
- **NS Records:** nile.ns.cloudflare.com, rita.ns.cloudflare.com
- **Tunnel:** Routes to localhost:5000

## SAP B1 Connection

```
Server: WIN-GJGQ73V0C3K
CompanyDB: Molas_Lubes_LTD
DbType: MSSQL2016
LicenseServer: WIN-GJGQ73V0C3K:30000
SLDServer: WIN-GJGQ73V0C3K:40000
```

## Deployment

See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) for production deployment guide.

## License

LGPL-3
