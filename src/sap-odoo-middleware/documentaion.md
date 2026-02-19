
SAP B1 ↔ Odoo 19 Integration Middleware
Technical Design Blueprint
For GitHub Copilot Implementation


Project	SAP B1 – Odoo 19 ERP Integration
Component	ASP.NET Core Middleware API
Connectivity	Cloudflare Tunnel (Zero Trust)
Version	1.0 – February 2026
Platform	.NET 8 / ASP.NET Core Web API
SAP Access	Direct SQL Server + Service Layer REST
Staging DB	Neon (PostgreSQL)
 Table of Contents
Table of Contents	2
1. Executive Summary	4
1.1 Key Design Principles	4
2. System Architecture	5
2.1 High-Level Data Flow	5
2.1.1 Odoo → Cloudflare → Middleware → SAP (Pull/Read)	5
2.1.2 Odoo → Cloudflare → Middleware → SAP (Push/Write)	5
2.1.3 Middleware → Odoo (Background Push)	5
2.2 Network Architecture	5
2.3 Authentication Flow	5
3. Technology Stack	7
3.1 NuGet Packages	7
4. Project Structure	9
5. API Endpoint Specification	11
5.1 Standard Response Envelope	11
5.2 Products Endpoints	11
5.2.1 GET /api/products Response Model	11
5.3 Inventory Endpoints	12
5.4 Business Partners Endpoints	12
5.5 Orders and Documents Endpoints	12
5.6 Webhook Endpoints (Odoo → Middleware)	13
5.6.1 POST /api/webhooks/delivery-confirmation Request Body	13
5.7 Sync and Health Endpoints	13
6. SAP B1 SQL Database Reference	15
6.1 Core Tables	15
6.2 Key SQL Queries	15
7. SAP B1 Service Layer Integration (Writes)	17
7.1 Service Layer Configuration	17
7.2 Service Layer Client Implementation	17
8. Neon (PostgreSQL) Staging Database Schema	18
8.1 Schema: sync_state	18
8.2 Schema: sync_queue	18
8.3 Schema: audit_log	19
9. SAP ↔ Odoo Field Mapping	20
9.1 Product Mapping (OITM → product.product)	20
9.2 Business Partner Mapping (OCRD → res.partner)	20
9.3 Sales Order Mapping (ORDR → sale.order)	21
10. Synchronization Engine	22
10.1 Sync Patterns	22
10.2 Sync Schedule	22
10.3 Queue Processing Logic	22
10.4 Change Detection Logic	23
11. Configuration Reference	24
11.1 appsettings.json Structure	24
11.2 Environment Variables	25
12. Cloudflare Tunnel Configuration	26
12.1 Tunnel Configuration File	26
12.2 Recommended Cloudflare Dashboard Settings	26
13. Application Startup (Program.cs)	27
14. Error Handling and Resilience	29
14.1 Retry Policy (Polly)	29
14.2 Global Exception Handler	29
14.3 Error Codes	29
15. CI/CD Pipeline (GitHub Actions)	31
15.1 Build Workflow (.github/workflows/build.yml)	31
15.2 Deploy Workflow (.github/workflows/deploy.yml)	31
15.3 Self-Hosted Runner Setup	31
16. Implementation Phases	32
Phase 1: Foundation (Week 1–2)	32
Phase 2: Read Endpoints (Week 3)	32
Phase 3: SAP Service Layer + Write Operations (Week 4)	32
Phase 4: Sync Engine (Week 5–6)	32
Phase 5: Production Hardening (Week 7–8)	33

 1. Executive Summary
This document provides a complete technical design blueprint for a .NET middleware API that integrates SAP Business One (on-premises, SQL Server) with Odoo 19. The middleware is exposed securely via Cloudflare Tunnel, eliminating the need for open inbound ports on the server. Odoo connects to the middleware through a public Cloudflare hostname over HTTPS.

SAP B1 serves as the single source of truth for inventory quantities, item valuations, and financial data. Odoo handles delivery workflows, sales operations, and profitability calculations. The middleware bridges these systems with bidirectional data synchronization, queue-based reliability, and comprehensive audit logging.

1.1 Key Design Principles
•	SAP as Source of Truth: All inventory quantities, costs, and valuations originate from SAP. Odoo stores SAP data in custom fields (x_neon_*) and keeps standard_price at zero.
•	Zero Trust Network: Cloudflare Tunnel provides secure connectivity without exposing server ports. API key + Cloudflare header validation for dual-layer authentication.
•	Read via SQL, Write via Service Layer: Direct SQL Server queries for fast reads; SAP B1 Service Layer REST API for writes to preserve business logic.
•	Queue-Based Reliability: All sync operations go through a Neon-hosted queue with retry logic, dead-letter handling, and full audit trail.
•	Idempotent Operations: Every sync operation must be safely re-runnable using entity references as natural keys.
 2. System Architecture
2.1 High-Level Data Flow
The system has three main data flow patterns:

2.1.1 Odoo → Cloudflare → Middleware → SAP (Pull/Read)
Odoo custom modules call the middleware API through the Cloudflare tunnel hostname. The middleware queries SAP B1 SQL Server directly and returns structured JSON responses. Used for: product catalog sync, stock level queries, business partner lookups, price list retrieval.

2.1.2 Odoo → Cloudflare → Middleware → SAP (Push/Write)
Odoo sends data to middleware webhook endpoints (e.g., delivery confirmations). The middleware translates and writes to SAP via the Service Layer REST API. Used for: delivery confirmations, sales order creation, invoice sync.

2.1.3 Middleware → Odoo (Background Push)
Hangfire background jobs detect changes in SAP SQL Server and push updates to Odoo via JSON-RPC. Used for: stock quantity updates, price changes, new product notifications, COGS sync.

2.2 Network Architecture
The Cloudflare Tunnel creates an outbound-only encrypted connection from the on-premises server to Cloudflare edge. No inbound firewall rules are required.

Odoo 19 (Cloud)	Cloudflare Edge	Tunnel (cloudflared)	On-Prem Server
Custom modules call HTTPS endpoint: sap-api.yourcompany.com	Zero Trust policies, WAF, Rate Limiting, API Shield, DDoS protection	Outbound-only connection from server to Cloudflare. No open ports required	ASP.NET Core API on localhost:5000. Direct SQL Server access to SAP B1. Service Layer for writes

2.3 Authentication Flow
Dual-layer authentication ensures only authorized Odoo instances can access the middleware:

1.	Odoo sends HTTPS request with X-Api-Key header to sap-api.yourcompany.com
2.	Cloudflare validates the request against Zero Trust access policies (optional: mTLS client certificate)
3.	Cloudflare routes through tunnel to localhost:5000, adding CF-Ray and CF-Connecting-IP headers
4.	Middleware validates: (a) CF-Ray header exists (request came through Cloudflare), (b) X-Api-Key matches stored key
5.	Request proceeds to controller. All requests are logged to Neon audit table.
 3. Technology Stack
Component	Technology	Purpose
Runtime	.NET 8 / ASP.NET Core	Middleware API framework
SAP Data Access (Read)	Dapper + Microsoft.Data.SqlClient	Direct SQL queries to SAP B1 database
SAP Data Access (Write)	SAP B1 Service Layer (REST)	Create/update SAP documents respecting business logic
Staging Database	Neon (PostgreSQL) + Npgsql	Sync queue, state tracking, audit logs
Background Jobs	Hangfire + SQL Server/PostgreSQL	Scheduled sync, change detection, retry processing
Authentication	Custom API Key Middleware	X-Api-Key header validation + CF header check
Network Security	Cloudflare Tunnel (cloudflared)	Zero Trust secure tunnel, no open inbound ports
Logging	Serilog	Structured logging to console + file + Neon
API Documentation	Swashbuckle (Swagger/OpenAPI)	Auto-generated API docs at /swagger
Serialization	System.Text.Json	JSON serialization with snake_case naming policy
CI/CD	GitHub Actions	Build, test, deploy via self-hosted runner
Health Checks	ASP.NET Core Health Checks	SAP SQL, Neon DB, SAP Service Layer connectivity

3.1 NuGet Packages

Package	Version	Purpose
Microsoft.Data.SqlClient	5.2+	SQL Server connection to SAP B1 database
Dapper	2.1+	Lightweight ORM for SQL query mapping
Npgsql.EntityFrameworkCore.PostgreSQL	8.0+	Neon PostgreSQL connection via EF Core
Hangfire.Core + Hangfire.AspNetCore	1.8+	Background job scheduling and processing
Hangfire.PostgreSql	1.20+	Hangfire storage backend using Neon
Serilog.AspNetCore	8.0+	Structured logging framework
Serilog.Sinks.File	5.0+	File-based log output
Swashbuckle.AspNetCore	6.5+	Swagger/OpenAPI documentation generation
Polly	8.0+	Resilience and retry policies
Microsoft.Extensions.Diagnostics.HealthChecks	8.0+	Health check infrastructure
FluentValidation.AspNetCore	11.0+	Request model validation
 4. Project Structure
The solution follows a clean architecture pattern with clear separation between API controllers, business services, and data access repositories.

sap-odoo-middleware/
│
├── .github/workflows/
│   ├── build.yml                    # CI: restore, build, test on every push/PR
│   └── deploy.yml                   # CD: publish + deploy to server on main push
│
├── src/SapOdooMiddleware/
│   ├── Program.cs                   # App entry, DI registration, middleware pipeline
│   ├── appsettings.json             # Configuration (non-sensitive defaults)
│   ├── appsettings.Development.json # Dev overrides
│   │
│   ├── Auth/
│   │   ├── ApiKeyMiddleware.cs          # Validates X-Api-Key header
│   │   └── CloudflareValidator.cs       # Validates CF-Ray, CF-Connecting-IP
│   │
│   ├── Controllers/
│   │   ├── ProductsController.cs        # GET /api/products, /api/products/{code}
│   │   ├── InventoryController.cs       # GET /api/inventory/stock, /valuations
│   │   ├── BusinessPartnersController.cs# GET /api/partners/customers, /suppliers
│   │   ├── OrdersController.cs          # GET+POST /api/orders/sales-order, /invoices
│   │   ├── WebhooksController.cs        # POST /api/webhooks/delivery, /invoice-cogs
│   │   ├── SyncController.cs            # POST /api/sync/trigger, GET /api/sync/status
│   │   └── HealthController.cs          # GET /api/health (SAP, Neon, Service Layer)
│   │
│   ├── Services/
│   │   ├── Sap/
│   │   │   ├── ISapSqlRepository.cs         # Interface for SAP SQL reads
│   │   │   ├── SapSqlRepository.cs          # Dapper-based SAP SQL queries
│   │   │   ├── ISapServiceLayerClient.cs    # Interface for SAP REST writes
│   │   │   └── SapServiceLayerClient.cs     # HttpClient to SAP Service Layer
│   │   ├── Odoo/
│   │   │   ├── IOdooJsonRpcClient.cs        # Interface for Odoo push
│   │   │   └── OdooJsonRpcClient.cs         # JSON-RPC calls to Odoo
│   │   ├── Sync/
│   │   │   ├── ProductSyncService.cs        # Product catalog sync logic
│   │   │   ├── StockSyncService.cs          # Stock quantity sync logic
│   │   │   ├── PriceSyncService.cs          # Price/valuation sync logic
│   │   │   ├── OrderSyncService.cs          # Order/invoice sync logic
│   │   │   ├── ChangeDetectorJob.cs         # Hangfire: detect SAP changes
│   │   │   ├── QueueProcessorJob.cs         # Hangfire: process sync queue
│   │   │   └── RetryPolicy.cs               # Polly retry/circuit breaker config
│   │   └── Neon/
│   │       ├── NeonDbContext.cs             # EF Core DbContext
│   │       ├── SyncStateRepository.cs       # Track last sync timestamps
│   │       ├── SyncQueueRepository.cs       # Enqueue/dequeue sync items
│   │       └── AuditLogRepository.cs        # Write audit log entries
│   │
│   ├── Models/
│   │   ├── Sap/                         # DTOs mapped from SAP tables
│   │   │   ├── SapItem.cs
│   │   │   ├── SapWarehouseStock.cs
│   │   │   ├── SapBusinessPartner.cs
│   │   │   ├── SapSalesOrder.cs
│   │   │   ├── SapInvoice.cs
│   │   │   └── SapPriceList.cs
│   │   ├── Odoo/                        # DTOs for Odoo JSON-RPC calls
│   │   │   ├── OdooProductUpdate.cs
│   │   │   ├── OdooStockUpdate.cs
│   │   │   └── OdooDeliveryConfirmation.cs
│   │   ├── Sync/                        # Queue and state entities
│   │   │   ├── SyncQueueItem.cs
│   │   │   ├── SyncState.cs
│   │   │   └── AuditLogEntry.cs
│   │   └── Api/                         # Request/response DTOs
│   │       ├── ApiResponse.cs               # Standard envelope {data,meta,errors}
│   │       ├── PaginatedResponse.cs
│   │       └── WebhookPayloads.cs
│   │
│   └── Configuration/
│       ├── SapSettings.cs               # SAP SQL + Service Layer config
│       ├── OdooSettings.cs              # Odoo URL, DB, credentials
│       ├── CloudflareSettings.cs        # Tunnel validation config
│       ├── NeonSettings.cs              # Neon connection string
│       └── SyncSettings.cs              # Intervals, batch sizes, retry config
│
├── tests/SapOdooMiddleware.Tests/
│   ├── Controllers/                 # Controller unit tests
│   ├── Services/                    # Service unit tests
│   └── Integration/                 # Integration tests with test DB
│
├── infrastructure/
│   ├── cloudflared/config.yml       # Cloudflare Tunnel configuration
│   ├── scripts/
│   │   ├── install-service.ps1         # Install API as Windows Service
│   │   ├── setup-tunnel.ps1            # Cloudflare tunnel setup helper
│   │   └── seed-neon.sql               # Neon database schema + seed data
│   └── docker/                      # Optional: for local dev/testing
│       └── docker-compose.yml
│
├── docs/
│   ├── API.md                       # Complete API endpoint documentation
│   ├── FIELD_MAPPING.md             # SAP table ↔ Odoo field mapping reference
│   ├── DEPLOYMENT.md                # Server deployment runbook
│   └── TROUBLESHOOTING.md           # Common issues and solutions
│
├── SapOdooMiddleware.sln
├── .gitignore
├── .editorconfig
└── README.md
 5. API Endpoint Specification
All endpoints require X-Api-Key header. All responses use a standard envelope format. Dates are ISO 8601. All numeric values use decimal precision matching SAP.

5.1 Standard Response Envelope
{
  "success": true,
  "data": { ... },
  "meta": {
    "timestamp": "2026-02-20T10:00:00Z",
    "page": 1,
    "page_size": 50,
    "total_count": 1234
  },
  "errors": []
}

5.2 Products Endpoints
Method	Endpoint	Description
GET	/api/products	List all products with pagination. Query: ?page=1&size=50&search=keyword
GET	/api/products/{itemCode}	Single product with full details including warehouse stock breakdown
GET	/api/products/modified	Products modified since timestamp. Query: ?since=2026-02-20T00:00:00Z
GET	/api/products/{itemCode}/stock	Warehouse-level stock for a specific product
GET	/api/products/{itemCode}/price-lists	All price lists applicable to this product

5.2.1 GET /api/products Response Model
{
  "item_code": "A00001",            // OITM.ItemCode
  "item_name": "Widget Pro",         // OITM.ItemName
  "foreign_name": "...",             // OITM.FrgnName
  "item_group": 100,                // OITM.ItmsGrpCod
  "item_group_name": "Finished",    // OITB.ItmsGrpNam (joined)
  "bar_code": "1234567890",         // OITM.CodeBars
  "on_hand": 150.000,              // OITM.OnHand (total across warehouses)
  "is_committed": 30.000,          // OITM.IsCommited
  "on_order": 50.000,              // OITM.OnOrder
  "available": 120.000,            // OnHand - IsCommited
  "avg_price": 25.50,              // OITM.AvgPrice
  "last_purchase_price": 24.00,    // OITM.LastPurPrc
  "default_warehouse": "01",       // OITM.DfltWH
  "uom": "Each",                   // OITM.InvntryUom
  "active": true,                  // OITM.frozenFor == "N"
  "update_date": "2026-02-20",     // OITM.UpdateDate
  "create_date": "2024-01-15",     // OITM.CreateDate
  "warehouses": [                  // From OITW join
    {
      "warehouse_code": "01",
      "warehouse_name": "Main",
      "on_hand": 100.000,
      "is_committed": 20.000,
      "on_order": 50.000,
      "available": 80.000
    }
  ]
}

5.3 Inventory Endpoints
Method	Endpoint	Description
GET	/api/inventory/stock	All warehouse stock levels. Query: ?warehouse=01&modified_since=...
GET	/api/inventory/valuations	Item valuations (avg cost, last purchase, std cost). Query: ?group=100
GET	/api/inventory/warehouse-list	List of all warehouses (OWHS)

5.4 Business Partners Endpoints
Method	Endpoint	Description
GET	/api/partners/customers	Customer list (OCRD CardType=C). Query: ?page, ?search, ?modified_since
GET	/api/partners/customers/{cardCode}	Single customer with addresses (CRD1), contacts (OCPR)
GET	/api/partners/suppliers	Supplier list (OCRD CardType=S). Same query params
GET	/api/partners/suppliers/{cardCode}	Single supplier with full details

5.5 Orders and Documents Endpoints
Method	Endpoint	Description
GET	/api/orders/sales-orders	List sales orders. Query: ?status=open&customer=C00001&from=&to=
GET	/api/orders/sales-orders/{docEntry}	Single sales order with line items (ORDR + RDR1)
POST	/api/orders/sales-order	Create sales order in SAP via Service Layer
GET	/api/orders/invoices	AR Invoices (OINV). Query: ?status, ?customer, ?from, ?to
GET	/api/orders/invoices/{docEntry}	Single invoice with lines (OINV + INV1)
GET	/api/orders/deliveries	Deliveries (ODLN). Query: same filters
GET	/api/orders/purchase-orders	Purchase orders (OPOR). Query: same filters

5.6 Webhook Endpoints (Odoo → Middleware)
Method	Endpoint	Description
POST	/api/webhooks/delivery-confirmation	Odoo sends delivery done event. Creates Delivery in SAP
POST	/api/webhooks/invoice-cogs	Odoo sends invoice with COGS. Syncs to SAP AR Invoice
POST	/api/webhooks/stock-adjustment	Odoo sends stock adjustment. Creates Goods Receipt/Issue in SAP

5.6.1 POST /api/webhooks/delivery-confirmation Request Body
{
  "odoo_delivery_id": 1234,
  "odoo_sale_order_name": "SO00045",
  "sap_sales_order_doc_entry": 890,
  "customer_card_code": "C00001",
  "delivery_date": "2026-02-20",
  "lines": [
    {
      "item_code": "A00001",
      "quantity": 10.0,
      "warehouse_code": "01",
      "uom": "Each",
      "base_line_num": 0
    }
  ]
}

5.7 Sync and Health Endpoints
Method	Endpoint	Description
POST	/api/sync/trigger	Manually trigger a sync. Body: {"sync_type": "products|stock|prices|all"}
GET	/api/sync/status	Returns last sync timestamps, queue depth, error counts
GET	/api/sync/queue	View pending/failed queue items. Query: ?status=failed&limit=50
POST	/api/sync/queue/{id}/retry	Retry a specific failed queue item
GET	/api/health	Health check: SAP SQL, Neon DB, SAP Service Layer connectivity
GET	/api/health/detailed	Detailed health with response times and version info
 6. SAP B1 SQL Database Reference
The following tables are the primary data sources for the middleware. All queries use READ-ONLY access. Never write directly to SAP SQL tables.

6.1 Core Tables
Entity	Header Table	Lines Table	Key Columns
Items	OITM	OITW (warehouse)	ItemCode, ItemName, OnHand, AvgPrice, IsCommited, OnOrder
Business Partners	OCRD	CRD1 (addresses)	CardCode, CardName, CardType (C/S/L), Phone1, E_Mail
Sales Orders	ORDR	RDR1	DocEntry, DocNum, CardCode, DocDate, DocTotal, DocStatus
AR Invoices	OINV	INV1	DocEntry, DocNum, CardCode, DocDate, DocTotal, DocStatus
Deliveries	ODLN	DLN1	DocEntry, DocNum, CardCode, DocDate, DocStatus
Purchase Orders	OPOR	POR1	DocEntry, DocNum, CardCode, DocDate, DocTotal
Goods Receipt	OIGN	IGN1	DocEntry, DocNum, DocDate
Goods Issue	OIGE	IGE1	DocEntry, DocNum, DocDate
Inventory Transfer	OWTR	WTR1	DocEntry, DocNum, Filler (from), ToWhsCode
Warehouses	OWHS	—	WhsCode, WhsName, Location
Item Groups	OITB	—	ItmsGrpCod, ItmsGrpNam
Price Lists	OPLN	ITM1	ListNum, ListName, ItemCode, Price, Currency
Chart of Accounts	OACT	—	AcctCode, AcctName, ActType

6.2 Key SQL Queries

Products with Warehouse Stock:
SELECT T0.ItemCode, T0.ItemName, T0.FrgnName, T0.ItmsGrpCod,
       T0.OnHand, T0.IsCommited, T0.OnOrder,
       T0.AvgPrice, T0.LastPurPrc, T0.DfltWH,
       T0.InvntryUom, T0.CodeBars, T0.frozenFor,
       T0.UpdateDate, T0.CreateDate,
       T1.ItmsGrpNam
FROM OITM T0
LEFT JOIN OITB T1 ON T0.ItmsGrpCod = T1.ItmsGrpCod
WHERE T0.UpdateDate >= @since OR @since IS NULL
ORDER BY T0.ItemCode
OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY

Warehouse-Level Stock:
SELECT T0.ItemCode, T0.WhsCode, T1.WhsName,
       T0.OnHand, T0.IsCommited, T0.OnOrder,
       (T0.OnHand - T0.IsCommited) AS Available
FROM OITW T0
INNER JOIN OWHS T1 ON T0.WhsCode = T1.WhsCode
WHERE T0.ItemCode = @itemCode
  AND (T0.OnHand <> 0 OR T0.IsCommited <> 0 OR T0.OnOrder <> 0)

Customers:
SELECT T0.CardCode, T0.CardName, T0.CardFName,
       T0.Phone1, T0.Phone2, T0.E_Mail,
       T0.Balance, T0.Currency, T0.GroupCode,
       T0.frozenFor, T0.UpdateDate, T0.CreateDate
FROM OCRD T0
WHERE T0.CardType = 'C'
  AND (T0.UpdateDate >= @since OR @since IS NULL)
ORDER BY T0.CardCode

Sales Orders (Open):
SELECT T0.DocEntry, T0.DocNum, T0.CardCode, T0.CardName,
       T0.DocDate, T0.DocDueDate, T0.DocTotal, T0.DocCur,
       T0.DocStatus, T0.Comments,
       T1.LineNum, T1.ItemCode, T1.Dscription, T1.Quantity,
       T1.Price, T1.LineTotal, T1.WhsCode, T1.LineStatus
FROM ORDR T0
INNER JOIN RDR1 T1 ON T0.DocEntry = T1.DocEntry
WHERE T0.DocStatus = 'O'
  AND T0.DocDate BETWEEN @from AND @to
ORDER BY T0.DocEntry, T1.LineNum
 7. SAP B1 Service Layer Integration (Writes)
All write operations to SAP go through the Service Layer REST API to ensure business logic, validations, and audit trails are maintained. The Service Layer is built into SAP B1 9.2+ and runs on the SAP server.

7.1 Service Layer Configuration
Base URL: https://<sap-server>:50000/b1s/v1/
Authentication: POST /Login with CompanyDB, UserName, Password
Session: Cookie-based (B1SESSION), 30-min timeout
Content-Type: application/json

7.2 Service Layer Client Implementation
The SapServiceLayerClient class must handle session management with automatic re-login on 401 responses. Implement a session cache that re-authenticates 5 minutes before expiry.

Login: POST /Login { CompanyDB, UserName, Password } → Store B1SESSION cookie
Create Sales Order: POST /Orders { CardCode, DocDate, DocumentLines: [...] }
Create Delivery: POST /DeliveryNotes { CardCode, DocDate, DocumentLines: [{ BaseEntry, BaseLine, BaseType: 17 }] }
Create AR Invoice: POST /Invoices { CardCode, DocDate, DocumentLines: [...] }
Create Goods Receipt: POST /InventoryGenEntries { DocumentLines: [{ ItemCode, Quantity, WarehouseCode }] }

Important: Always include BaseType, BaseEntry, and BaseLine when creating documents linked to other SAP documents (e.g., Delivery from Sales Order uses BaseType: 17).
 8. Neon (PostgreSQL) Staging Database Schema
The Neon database serves three purposes: sync state tracking, reliable message queuing with retry, and comprehensive audit logging. All middleware sync operations are mediated through this database.

8.1 Schema: sync_state
CREATE TABLE sync_state (
    sync_type       VARCHAR(50) PRIMARY KEY,
    last_sync_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_record_count INT DEFAULT 0,
    last_duration_ms  INT DEFAULT 0,
    status          VARCHAR(20) DEFAULT 'success',
    error_message   TEXT,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Seed initial state
INSERT INTO sync_state (sync_type, last_sync_at) VALUES
('products_pull', '2020-01-01T00:00:00Z'),
('stock_push', '2020-01-01T00:00:00Z'),
('prices_pull', '2020-01-01T00:00:00Z'),
('partners_pull', '2020-01-01T00:00:00Z'),
('orders_pull', '2020-01-01T00:00:00Z');

8.2 Schema: sync_queue
CREATE TABLE sync_queue (
    id              SERIAL PRIMARY KEY,
    direction       VARCHAR(15) NOT NULL,      -- 'sap_to_odoo' | 'odoo_to_sap'
    entity_type     VARCHAR(50) NOT NULL,      -- 'product' | 'stock' | 'invoice' | etc.
    entity_ref      VARCHAR(100) NOT NULL,     -- SAP ItemCode or Odoo record ID
    operation       VARCHAR(20) NOT NULL,      -- 'create' | 'update' | 'delete'
    payload         JSONB NOT NULL,            -- Full data payload
    status          VARCHAR(20) DEFAULT 'pending',  -- pending | processing | completed | failed | dead_letter
    priority        INT DEFAULT 5,             -- 1=highest, 10=lowest
    attempts        INT DEFAULT 0,
    max_attempts    INT DEFAULT 5,
    next_retry_at   TIMESTAMPTZ,
    last_error      TEXT,
    created_at      TIMESTAMPTZ DEFAULT NOW(),
    started_at      TIMESTAMPTZ,
    completed_at    TIMESTAMPTZ
);

CREATE INDEX idx_queue_status_priority ON sync_queue(status, priority, created_at);
CREATE INDEX idx_queue_entity ON sync_queue(entity_type, entity_ref);
CREATE INDEX idx_queue_retry ON sync_queue(next_retry_at) WHERE status = 'failed';

8.3 Schema: audit_log
CREATE TABLE audit_log (
    id              BIGSERIAL PRIMARY KEY,
    timestamp       TIMESTAMPTZ DEFAULT NOW(),
    sync_type       VARCHAR(50) NOT NULL,
    direction       VARCHAR(15) NOT NULL,
    entity_type     VARCHAR(50),
    entity_ref      VARCHAR(100),
    operation       VARCHAR(20),
    records_affected INT DEFAULT 0,
    duration_ms     INT,
    status          VARCHAR(20) NOT NULL,     -- 'success' | 'error' | 'partial'
    request_id      UUID,                     -- Correlation ID from HTTP request
    source_ip       VARCHAR(45),              -- CF-Connecting-IP
    error_details   JSONB,
    metadata        JSONB                     -- Additional context
);

CREATE INDEX idx_audit_timestamp ON audit_log(timestamp DESC);
CREATE INDEX idx_audit_entity ON audit_log(entity_type, entity_ref);
CREATE INDEX idx_audit_status ON audit_log(status) WHERE status != 'success';
 9. SAP ↔ Odoo Field Mapping
This section defines how SAP B1 fields map to Odoo 19 fields. Custom fields in Odoo use the x_neon_ prefix. SAP is the source of truth for inventory and financial data; Odoo stores SAP values in custom fields and keeps standard_price at zero.

9.1 Product Mapping (OITM → product.product)
SAP Field (OITM)	Odoo Field	Notes
ItemCode	default_code	Natural key for matching
ItemName	name	Product display name
FrgnName	x_neon_foreign_name	Custom field
ItmsGrpCod	categ_id (mapped)	Map SAP group to Odoo category via lookup table
CodeBars	barcode	EAN/UPC barcode
OnHand	x_neon_onhand_sap	Custom field – displayed prominently in Odoo UI
IsCommited	x_neon_committed_sap	Custom field
OnOrder	x_neon_onorder_sap	Custom field
OnHand - IsCommited	x_neon_available_cache	Computed by middleware, cached in Odoo
AvgPrice	x_sap_avg_cost	Custom field – never set as standard_price
LastPurPrc	x_sap_last_purchase	Custom field
DfltWH	x_neon_default_wh	Custom field
InvntryUom	uom_id (mapped)	Map to Odoo UoM via lookup
frozenFor	active	Y → False, N → True (inverted)
—	standard_price	ALWAYS 0 – SAP is source of truth for valuation
—	type	ALWAYS 'product' (Storable Product)

9.2 Business Partner Mapping (OCRD → res.partner)
SAP Field (OCRD)	Odoo Field	Notes
CardCode	ref	External reference / SAP code
CardName	name	Partner display name
Phone1	phone	Primary phone
Phone2	mobile	Secondary phone / mobile
E_Mail	email	Email address
CRD1.Street	street	From address table, AdresType='B' for billing
CRD1.City	city	From address table
CRD1.Country	country_id (mapped)	Map country code to Odoo country
CardType='C'	customer_rank = 1	Mark as customer
CardType='S'	supplier_rank = 1	Mark as supplier
Balance	x_neon_sap_balance	Custom field – current AR/AP balance
GroupCode	x_neon_sap_group	Custom field – SAP BP group

9.3 Sales Order Mapping (ORDR → sale.order)
SAP Field	Odoo Field	Notes
ORDR.DocEntry	x_neon_sap_doc_entry	Custom field – SAP internal ID
ORDR.DocNum	x_neon_sap_doc_num	Custom field – SAP document number
ORDR.CardCode	partner_id (lookup)	Match via ref = CardCode
ORDR.DocDate	date_order	Order date
ORDR.DocDueDate	commitment_date	Expected delivery date
RDR1.ItemCode	order_line.product_id (lookup)	Match via default_code
RDR1.Quantity	order_line.product_uom_qty	Line quantity
RDR1.Price	order_line.price_unit	Unit price
RDR1.WhsCode	order_line.x_neon_warehouse	Custom field on order line
 10. Synchronization Engine
10.1 Sync Patterns

Pattern	Direction	Trigger	Data
Scheduled Pull	SAP → Odoo	Cron (via Hangfire)	Products, Prices, Business Partners
Change Detection Push	SAP → Odoo	Hangfire polls SAP UpdateDate	Stock levels, Price changes
Webhook (Real-time)	Odoo → SAP	Odoo HTTP call on event	Delivery confirmations, Invoice COGS
On-Demand	SAP → Odoo	Manual API trigger	Full resync of any entity

10.2 Sync Schedule
Job	Interval	Direction	Description
ProductSyncJob	Every 15 min	SAP → Odoo	Sync new/modified products
StockSyncJob	Every 5 min	SAP → Odoo	Push stock quantity changes
PriceSyncJob	Every 30 min	SAP → Odoo	Sync price list updates
PartnerSyncJob	Every 60 min	SAP → Odoo	Sync customer/supplier changes
QueueProcessorJob	Every 1 min	Both	Process pending queue items
DeadLetterCleanupJob	Daily at 2 AM	N/A	Archive dead-letter items older than 30 days
HealthCheckJob	Every 5 min	N/A	Verify SAP SQL, Neon, Odoo connectivity

10.3 Queue Processing Logic
The QueueProcessorJob runs every minute and processes items in priority order:

6.	Fetch up to 50 items with status = 'pending' or (status = 'failed' AND next_retry_at <= NOW()), ordered by priority ASC, created_at ASC
7.	Set status = 'processing', started_at = NOW(), increment attempts
8.	Execute the sync operation (call Odoo JSON-RPC or SAP Service Layer)
9.	On success: set status = 'completed', completed_at = NOW(). Write audit log entry.
10.	On failure: if attempts < max_attempts, set status = 'failed', next_retry_at = NOW() + exponential_backoff(attempts). If attempts >= max_attempts, set status = 'dead_letter'.
11.	Exponential backoff formula: delay = min(base_delay * 2^(attempts-1), max_delay). Default: base = 60s, max = 3600s.

10.4 Change Detection Logic
The ChangeDetectorJob polls SAP SQL Server for rows with UpdateDate greater than the last sync timestamp stored in sync_state. For each detected change, it enqueues a sync_queue item. This approach avoids the need for SQL Server triggers or CDC configuration.

// Pseudocode for ChangeDetectorJob
var lastSync = await neonDb.GetSyncState('stock_push');
var changes = await sapSql.Query(
    'SELECT ItemCode, WhsCode, OnHand, IsCommited FROM OITW T0 ' +
    'INNER JOIN OITM T1 ON T0.ItemCode = T1.ItemCode ' +
    'WHERE T1.UpdateDate >= @since', lastSync.LastSyncAt);

foreach (var change in changes) {
    await neonDb.EnqueueSync(new SyncQueueItem {
        Direction = 'sap_to_odoo',
        EntityType = 'stock',
        EntityRef = change.ItemCode,
        Operation = 'update',
        Payload = JsonSerializer.Serialize(change)
    });
}
await neonDb.UpdateSyncState('stock_push', DateTime.UtcNow, changes.Count);
 11. Configuration Reference
All sensitive values must be stored in environment variables or a secrets manager, never in appsettings.json committed to source control.

11.1 appsettings.json Structure
{
  "Sap": {
    "SqlConnectionString": "SET_VIA_ENVIRONMENT_VARIABLE",
    "ServiceLayerUrl": "https://localhost:50000/b1s/v1/",
    "ServiceLayerCompanyDb": "YOUR_SAP_DB",
    "ServiceLayerUser": "SET_VIA_ENVIRONMENT_VARIABLE",
    "ServiceLayerPassword": "SET_VIA_ENVIRONMENT_VARIABLE"
  },
  "Odoo": {
    "Url": "https://your-odoo-instance.com",
    "Database": "odoo_production",
    "User": "SET_VIA_ENVIRONMENT_VARIABLE",
    "Password": "SET_VIA_ENVIRONMENT_VARIABLE"
  },
  "Neon": {
    "ConnectionString": "SET_VIA_ENVIRONMENT_VARIABLE"
  },
  "Auth": {
    "ApiKey": "SET_VIA_ENVIRONMENT_VARIABLE",
    "RequireCloudflareHeaders": true
  },
  "Sync": {
    "ProductIntervalMinutes": 15,
    "StockIntervalMinutes": 5,
    "PriceIntervalMinutes": 30,
    "PartnerIntervalMinutes": 60,
    "QueueProcessorIntervalMinutes": 1,
    "QueueBatchSize": 50,
    "RetryBaseDelaySeconds": 60,
    "RetryMaxDelaySeconds": 3600,
    "MaxRetryAttempts": 5,
    "DefaultPageSize": 50
  },
  "Cloudflare": {
    "TunnelHostname": "sap-api.yourcompany.com",
    "ValidateCfHeaders": true
  },
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [
      { "Name": "Console" },
      { "Name": "File", "Args": { "path": "logs/middleware-.log", "rollingInterval": "Day" } }
    ]
  }
}

11.2 Environment Variables
Variable	Purpose
SAP__SqlConnectionString	SQL Server connection string for SAP B1 database
SAP__ServiceLayerUser	SAP Service Layer login username
SAP__ServiceLayerPassword	SAP Service Layer login password
ODOO__User	Odoo integration user login
ODOO__Password	Odoo integration user password
NEON__ConnectionString	Neon PostgreSQL connection string
AUTH__ApiKey	API key for middleware authentication
 12. Cloudflare Tunnel Configuration
The Cloudflare Tunnel is created via the Cloudflare dashboard UI. The cloudflared daemon runs as a Windows Service on the SAP server, establishing an outbound encrypted connection to Cloudflare edge.

12.1 Tunnel Configuration File
Located at: infrastructure/cloudflared/config.yml (reference only; UI-created tunnels are managed via dashboard)
tunnel: <TUNNEL_ID_FROM_DASHBOARD>
credentials-file: C:\cloudflared\<TUNNEL_ID>.json

ingress:
  - hostname: sap-api.yourcompany.com
    service: http://localhost:5000
    originRequest:
      noTLSVerify: true
      connectTimeout: 30s
      httpHostHeader: sap-api.yourcompany.com

  - service: http_status:404

12.2 Recommended Cloudflare Dashboard Settings
•	WAF: Enable managed ruleset for API protection
•	Rate Limiting: 100 requests/minute per IP for /api/* endpoints
•	API Shield: Upload OpenAPI spec for schema validation (optional)
•	Zero Trust Access Policy: Restrict /api/* to allowed IPs or service tokens (recommended for production)
•	SSL/TLS: Full (strict) mode for end-to-end encryption
 13. Application Startup (Program.cs)
The following is the complete dependency injection registration and middleware pipeline specification for Program.cs:

// Program.cs - Complete specification

var builder = WebApplication.CreateBuilder(args);

// 1. Configuration binding
builder.Services.Configure<SapSettings>(builder.Configuration.GetSection("Sap"));
builder.Services.Configure<OdooSettings>(builder.Configuration.GetSection("Odoo"));
builder.Services.Configure<NeonSettings>(builder.Configuration.GetSection("Neon"));
builder.Services.Configure<AuthSettings>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<SyncSettings>(builder.Configuration.GetSection("Sync"));

// 2. Serilog
builder.Host.UseSerilog((ctx, lc) => lc.ReadFrom.Configuration(ctx.Configuration));

// 3. Database contexts
builder.Services.AddDbContext<NeonDbContext>(o =>
    o.UseNpgsql(builder.Configuration["Neon:ConnectionString"]));

// 4. SAP services (Scoped - one per request)
builder.Services.AddScoped<ISapSqlRepository, SapSqlRepository>();
builder.Services.AddHttpClient<ISapServiceLayerClient, SapServiceLayerClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    });

// 5. Odoo client
builder.Services.AddHttpClient<IOdooJsonRpcClient, OdooJsonRpcClient>();

// 6. Sync services
builder.Services.AddScoped<ProductSyncService>();
builder.Services.AddScoped<StockSyncService>();
builder.Services.AddScoped<PriceSyncService>();
builder.Services.AddScoped<OrderSyncService>();

// 7. Neon repositories
builder.Services.AddScoped<SyncStateRepository>();
builder.Services.AddScoped<SyncQueueRepository>();
builder.Services.AddScoped<AuditLogRepository>();

// 8. Hangfire
builder.Services.AddHangfire(c => c.UsePostgreSqlStorage(
    builder.Configuration["Neon:ConnectionString"]));
builder.Services.AddHangfireServer();

// 9. Swagger + Health Checks + Controllers
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy =
        JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration["Neon:ConnectionString"], name: "neon")
    .AddSqlServer(builder.Configuration["Sap:SqlConnectionString"], name: "sap-sql");

var app = builder.Build();

// Middleware pipeline
app.UseSerilogRequestLogging();
app.UseMiddleware<CloudflareValidator>();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();
app.MapHealthChecks("/api/health");
app.UseHangfireDashboard("/hangfire");

// Register Hangfire recurring jobs
RecurringJob.AddOrUpdate<ChangeDetectorJob>("stock-sync",
    j => j.DetectAndPushStockChanges(), "*/5 * * * *");
RecurringJob.AddOrUpdate<ChangeDetectorJob>("product-sync",
    j => j.DetectAndPushProductChanges(), "*/15 * * * *");
RecurringJob.AddOrUpdate<QueueProcessorJob>("queue-processor",
    j => j.ProcessPendingItems(), "* * * * *");

app.Run();
 14. Error Handling and Resilience

14.1 Retry Policy (Polly)
All outbound HTTP calls (to SAP Service Layer and Odoo) use Polly retry policies with exponential backoff and circuit breaker patterns:

•	HTTP Retry: 3 retries with exponential backoff (2s, 4s, 8s) for transient HTTP errors (5xx, 408, 429)
•	Circuit Breaker: Break circuit after 5 consecutive failures. Half-open after 30 seconds.
•	Timeout: 30 second per-request timeout. 120 second total timeout for webhook processing.
•	SAP Session: Auto re-login on 401 response. Max 3 login attempts per minute.

14.2 Global Exception Handler
Implement a global exception handling middleware that catches all unhandled exceptions, logs them to Serilog and Neon audit_log, and returns a standardized error response:
{
  "success": false,
  "data": null,
  "errors": [
    {
      "code": "SAP_CONNECTION_FAILED",
      "message": "Unable to connect to SAP SQL Server",
      "detail": "Connection timeout after 30 seconds"
    }
  ]
}

14.3 Error Codes
Error Code	HTTP Status	Description
AUTH_INVALID_KEY	401	Missing or invalid X-Api-Key header
AUTH_NOT_CLOUDFLARE	403	Request did not come through Cloudflare tunnel
SAP_CONNECTION_FAILED	503	Cannot connect to SAP SQL Server
SAP_SL_AUTH_FAILED	502	SAP Service Layer login failed
SAP_SL_REQUEST_FAILED	502	SAP Service Layer returned an error
SAP_ENTITY_NOT_FOUND	404	Requested SAP entity does not exist
ODOO_CONNECTION_FAILED	502	Cannot connect to Odoo JSON-RPC
NEON_CONNECTION_FAILED	503	Cannot connect to Neon database
VALIDATION_ERROR	400	Request body validation failed
SYNC_QUEUE_FULL	429	Sync queue depth exceeds threshold
INTERNAL_ERROR	500	Unexpected internal error
 15. CI/CD Pipeline (GitHub Actions)

15.1 Build Workflow (.github/workflows/build.yml)
Triggers on every push and pull request. Runs on windows-latest. Steps: checkout, setup .NET 8, restore, build, run tests.

15.2 Deploy Workflow (.github/workflows/deploy.yml)
Triggers on push to main branch only. Uses a GitHub self-hosted runner installed on the on-premises server. Steps:

1.	Checkout code and setup .NET 8
2.	Run dotnet publish -c Release -o ./publish
3.	Stop the SapOdooMiddleware Windows Service
4.	Copy published files to C:\middleware\
5.	Start the SapOdooMiddleware Windows Service
6.	Run a health check against http://localhost:5000/api/health
7.	If health check fails, rollback to previous version from backup

15.3 Self-Hosted Runner Setup
A GitHub Actions self-hosted runner must be installed on the on-premises server to enable automated deployments. The runner connects outbound to GitHub (no inbound ports required). Install following GitHub documentation for Windows self-hosted runners.
 16. Implementation Phases

Phase 1: Foundation (Week 1–2)
•	Scaffold ASP.NET Core Web API project with solution structure from Section 4
•	Implement Program.cs with DI registration per Section 13
•	Implement ApiKeyMiddleware and CloudflareValidator (Auth/)
•	Implement SapSqlRepository with Dapper (products and stock queries from Section 6.2)
•	Implement ProductsController and InventoryController (read-only endpoints)
•	Setup Neon database with schema from Section 8
•	Implement standard ApiResponse envelope and error handling (Section 14)
•	Configure Serilog logging
•	Verify connectivity: middleware → SAP SQL and middleware → Neon
•	Test through Cloudflare Tunnel

Phase 2: Read Endpoints (Week 3)
•	Implement BusinessPartnersController
•	Implement OrdersController (GET endpoints for sales orders, invoices, deliveries)
•	Implement SyncController (status endpoint)
•	Implement HealthController with connectivity checks
•	Add Swagger/OpenAPI documentation
•	Add pagination, filtering, and sorting to all list endpoints
•	Write unit tests for repositories and controllers

Phase 3: SAP Service Layer + Write Operations (Week 4)
•	Implement SapServiceLayerClient with session management and auto-relogin
•	Implement POST /api/orders/sales-order (create SO in SAP)
•	Implement WebhooksController for delivery confirmations
•	Implement WebhooksController for invoice COGS sync
•	Add Polly retry policies for all outbound HTTP calls
•	Add FluentValidation for all POST request models

Phase 4: Sync Engine (Week 5–6)
•	Implement OdooJsonRpcClient (authenticate, search_read, write, create)
•	Implement SyncQueueRepository (enqueue, dequeue, retry, dead-letter)
•	Implement ChangeDetectorJob (stock and product change detection)
•	Implement QueueProcessorJob with exponential backoff retry
•	Implement ProductSyncService, StockSyncService, PriceSyncService
•	Configure Hangfire recurring jobs per Section 10.2
•	Implement AuditLogRepository and integrate logging throughout sync pipeline

Phase 5: Production Hardening (Week 7–8)
•	Setup GitHub Actions CI/CD per Section 15
•	Install self-hosted runner on server
•	Deploy as Windows Service
•	Configure Cloudflare WAF, rate limiting, and Zero Trust policies per Section 12.2
•	Load testing and performance tuning
•	Setup monitoring and alerting (Hangfire dashboard + health checks)
•	Write deployment runbook (docs/DEPLOYMENT.md)
•	User acceptance testing with Odoo custom modules


End of Blueprint Document
Version 1.0 – February 2026

