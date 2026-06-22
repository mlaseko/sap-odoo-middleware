# Product & Item Master — Odoo / SAP B1 Mapping Guide

## Overview

This document describes how products are structured in Odoo, how they map to SAP Business One item master data (OITM), and what the middleware expects when syncing transactional documents that reference items.

**Current state:** Product creation is manual (items must exist in SAP first). This document serves as the specification for automating bi-directional product sync.

---

## 1. Odoo Product Structure

Odoo uses a two-level model:

| Level | Model | Purpose | Example |
|-------|-------|---------|---------|
| Template | `product.template` | Parent container — shared name, category, UoM | "Shell Helix HX7 10W-40" |
| Variant | `product.product` | Sellable SKU — unique barcode, item code, stock | "Shell Helix HX7 10W-40 — 5L" |

If no variant attributes are defined, Odoo auto-creates a single variant per template.

### Required Fields for Product Creation

#### product.template (minimum)

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `name` | Char | Yes | — | Display name |
| `type` | Selection | Yes | `product` | `product` (storable), `consu` (consumable), `service` |
| `categ_id` | Many2one | Yes | "All" | Product category |
| `uom_id` | Many2one | Yes | "Units" | Sales/inventory UoM |
| `uom_po_id` | Many2one | No | same as `uom_id` | Purchase UoM |
| `list_price` | Float | No | 0.0 | Public sales price |
| `standard_price` | Float | No | 0.0 | Cost price (FIFO/Average) |
| `sale_ok` | Boolean | No | True | Available for sale |
| `purchase_ok` | Boolean | No | True | Available for purchase |
| `default_code` | Char | No | — | Internal reference (fallback for SAP item code) |
| `barcode` | Char | No | — | EAN-13 / UPC barcode |
| `taxes_id` | Many2many | No | — | Customer taxes |
| `supplier_taxes_id` | Many2many | No | — | Vendor taxes |
| `weight` | Float | No | 0.0 | Product weight (kg) |
| `volume` | Float | No | 0.0 | Product volume (m3) |

#### product.product (variant-level additions)

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `product_tmpl_id` | Many2one | Yes | Parent template |
| `default_code` | Char | No | Variant-specific internal reference |
| `barcode` | Char | No | Variant-specific barcode |
| `x_sap_item_code` | Char(50) | **Yes for SAP sync** | SAP B1 Item Code (OITM.ItemCode) |

---

## 2. SAP B1 Item Master (OITM)

### Key Fields

| SAP Field | Table | Type | Required | Description |
|-----------|-------|------|----------|-------------|
| `ItemCode` | OITM | Char(20) | Yes | **Primary key** — unique item identifier |
| `ItemName` | OITM | Char(200) | Yes | Item description |
| `ItemType` | OITM | Enum | Yes | `I` = Inventory, `L` = Labour, `T` = Travel |
| `ItmsGrpCod` | OITM | Int | Yes | Item group code (OITG.ItmsGrpCod) |
| `InvntryUom` | OITM | Char(20) | Yes | Inventory unit of measure |
| `SalUnitMsr` | OITM | Char(20) | No | Sales unit of measure |
| `BuyUnitMsr` | OITM | Char(20) | No | Purchase unit of measure |
| `DfltWH` | OITM | Char(8) | No | Default warehouse code |
| `OnHand` | OITM | Decimal | Auto | Current stock on hand |
| `AvgPrice` | OITM | Decimal | Auto | Moving average cost |
| `CodeBars` | OITM | Char(254) | No | Barcode |
| `SuppCatNum` | OITM | Char | No | Supplier catalog number |
| `Mainsupplier` | OITM | Char | No | Default vendor CardCode |
| `VatGourpSa` | OITM | Char | No | Output tax group |
| `VatGourpPu` | OITM | Char | No | Input tax group |
| `validFor` | OITM | Char(1) | Yes | `Y` = active, `N` = inactive |
| `SWeight1` | OITM | Decimal | No | Weight |
| `SVolume` | OITM | Decimal | No | Volume |

### Price Lists (ITM1)

| SAP Field | Table | Description |
|-----------|-------|-------------|
| `ItemCode` | ITM1 | Foreign key to OITM |
| `PriceList` | ITM1 | Price list number (from OPLN) |
| `Price` | ITM1 | Unit price in that list |
| `Currency` | ITM1 | Price currency code |

### UDFs (User-Defined Fields)

| UDF | Table | Purpose |
|-----|-------|---------|
| `U_Odoo_Product_ID` | OITM | Back-reference to Odoo `product.product.id` (proposed) |

---

## 3. Cross-System Mapping

### Primary Key Relationship

```
SAP B1:   OITM.ItemCode  (string, max 20 chars)
              ↕
Odoo:     product.product.x_sap_item_code  (Char(50), indexed, unique)
```

**ItemCode is the single cross-system reference.** There is no numeric ID stored in SAP pointing back to Odoo. The relationship is maintained through the item code string match.

### Field Mapping: Odoo ↔ SAP

| Odoo Field | Model | SAP Field | Table | Direction | Notes |
|------------|-------|-----------|-------|-----------|-------|
| `x_sap_item_code` | product.product | `ItemCode` | OITM | Odoo ↔ SAP | **Primary link** — unique, indexed |
| `name` | product.template | `ItemName` | OITM | Odoo → SAP | Product display name |
| `type` | product.template | `ItemType` | OITM | Odoo → SAP | `product`→`I`, `service`→`L` |
| `categ_id` | product.template | `ItmsGrpCod` | OITM | Odoo → SAP | Requires category mapping table |
| `uom_id.name` | product.template | `InvntryUom` | OITM | Odoo → SAP | Must match SAP UoM codes |
| `uom_id.name` | product.template | `SalUnitMsr` | OITM | Odoo → SAP | Sales unit |
| `uom_po_id.name` | product.template | `BuyUnitMsr` | OITM | Odoo → SAP | Purchase unit |
| `list_price` | product.template | `Price` | ITM1 | Odoo → SAP | Price list entry |
| `standard_price` | product.template | `AvgPrice` | OITM | SAP → Odoo | Cost (read-only in Odoo) |
| `barcode` | product.product | `CodeBars` | OITM | Odoo ↔ SAP | Barcode |
| `default_code` | product.product | `SuppCatNum` | OITM | Odoo → SAP | Internal reference (fallback) |
| `active` | product.product | `validFor` | OITM | Odoo → SAP | `True`→`Y`, `False`→`N` |
| `weight` | product.template | `SWeight1` | OITM | Odoo → SAP | Weight in kg |
| `volume` | product.template | `SVolume` | OITM | Odoo → SAP | Volume |
| `taxes_id` | product.template | `VatGourpSa` | OITM | Odoo → SAP | Requires tax group mapping |
| `x_neon_onhand_sap` | product.product | `OnHand` | OITM | SAP → Odoo | Stock qty (via Neon cache) |
| `x_sap_pricelist_num` | product.pricelist | `PriceList` | ITM1/OPLN | Odoo ↔ SAP | Price list ID |

### Item Code Resolution in Transactions

When building payloads for sales orders, invoices, returns, etc., Odoo resolves the SAP item code using this fallback chain:

```python
item_code = (
    line.product_id.x_sap_item_code    # Primary: SAP-specific code
    or line.product_id.default_code     # Fallback: Odoo internal reference
    or ""                               # Error: raises UserError
)
```

If neither field is populated, the sync job is blocked with a validation error.

---

## 4. Neon Cache Layer (Middleware Database)

The Neon PostgreSQL cache holds product data for two purposes:

### Stock Cache (SAP → Neon → Odoo)

| Neon Field | Source | Destination | Purpose |
|------------|--------|-------------|---------|
| `item_code` | OITM.ItemCode | Match to `x_sap_item_code` | Join key |
| `on_hand` | OITM.OnHand | `x_neon_onhand_sap` | Inventory levels |
| `available` | Calculated | `x_neon_available_cache` | Available to promise |
| `synced_at` | Auto | `x_neon_synced_at` | Freshness indicator |

### E-Commerce Product Data (Neon → Odoo)

Table: `NeonLiquiMolyProducts` (matched by `ArticleNumber` = `x_sap_item_code`)

| Neon Field | Odoo Field | Description |
|------------|------------|-------------|
| `ArticleNumber` | `x_sap_item_code` | Join key |
| `ShortName` | `x_ecom_short_name` | Marketing name |
| `Description` | `x_ecom_description` | Product description |
| `Category` | `x_ecom_category` | Product category |
| `SubCategory` | `x_ecom_subcategory` | Sub-category |
| `SpecGrade` | `x_ecom_spec_grade` | Specification grade |
| `PackSize` | `x_ecom_pack_size` | Pack size |
| `Liter` | `x_ecom_liter` | Volume in litres |
| `ImageUrl` | `x_ecom_image_url` | CDN image URL |
| `ProductUrl` | `x_ecom_product_url` | Product page URL |
| `Approvals` | `x_ecom_approvals` | OEM approvals list |
| `Specifications` | `x_ecom_specifications` | Technical specs |

---

## 5. How Items Flow in Existing Transactions

### Sales Order (Odoo → SAP)

```
Odoo sale.order.line
  └─ product_id.x_sap_item_code  →  middleware "item_code"  →  SAP RDR1.ItemCode
  └─ product_uom_qty             →  middleware "quantity"    →  SAP RDR1.Quantity
  └─ price_unit                  →  middleware "price"       →  SAP RDR1.Price
```

### Invoice (Odoo → SAP)

```
Odoo account.move.line
  └─ product_id.x_sap_item_code  →  middleware "item_code"  →  SAP INV1.ItemCode
  └─ quantity                     →  middleware "quantity"    →  SAP INV1.Quantity
  └─ price_unit                   →  middleware "price"       →  SAP INV1.Price
```

When invoice is created via Copy-To from delivery, middleware reads ItemCode from SAP delivery lines directly (no Odoo lookup needed).

### COGS (SAP → Odoo)

```
SAP INV1 (invoice lines)
  └─ ItemCode        →  middleware "item_code"       →  match to Odoo invoice line
  └─ GrossBuyPr      →  middleware "unit_cost"       →  COGS journal debit amount
  └─ Quantity        →  middleware "quantity"         →  multiplied by unit_cost
  └─ LineNum         →  middleware "line_num"         →  x_sap_invoice_linenum match
```

### Goods Return (Odoo → SAP)

```
Odoo stock.move (return picking lines)
  └─ product_id.x_sap_item_code  →  middleware "item_code"  →  SAP RDN1.ItemCode
  └─ quantity                     →  middleware "quantity"    →  SAP RDN1.Quantity
```

Middleware matches by ItemCode to original delivery lines (DLN1) to resolve `BaseLine`.

---

## 6. Middleware Product Resolution (Reverse Lookup)

When the middleware needs to find Odoo products from SAP data (e.g., COGS line matching), it uses the Odoo JSON-RPC API:

```csharp
// OdooJsonRpcService.cs — product resolution
var products = await SearchReadAsync("product.product",
    domain: [["id", "in", productIds]],
    fields: ["id", "x_sap_item_code"]
);

// Build lookup: Odoo product ID → SAP ItemCode
foreach (var p in products)
{
    productItemCodes[p.id] = p.x_sap_item_code;
}
```

This reverse lookup is used when:
- Building COGS journal entries (match SAP line ItemCode to Odoo invoice line product)
- Confirming deliveries (match SAP delivery lines to Odoo stock moves)

---

## 7. Constraints & Validation Rules

| Rule | Enforcement | Level |
|------|-------------|-------|
| `x_sap_item_code` must be unique | `@api.constrains` + SQL check | product.product |
| Item code required for SO export | Job validation → `UserError` | integration.job |
| Item code required for return export | Payload build validation | integration.job |
| Template `x_sap_item_code` is read-only | Computed field from first variant | product.template |
| No copy on duplicate | `copy=False` on `x_sap_item_code` | product.product |

---

## 8. Automation Requirements (Proposed)

To automate product creation (Odoo → SAP), the following components are needed:

### Middleware Endpoint (New)

```
POST /api/items         — Create item in SAP B1
PUT  /api/items/{code}  — Update existing item
GET  /api/items/{code}  — Read item details
```

**Proposed Request Payload:**

```json
{
  "item_code": "LM-1234",
  "item_name": "Shell Helix HX7 10W-40 5L",
  "item_type": "I",
  "items_group_code": 100,
  "inventory_uom": "Litre",
  "sales_uom": "Litre",
  "purchase_uom": "Litre",
  "default_warehouse": "WH01",
  "barcode": "5011987860100",
  "vat_group_sales": "A1",
  "vat_group_purchase": "A1",
  "weight": 4.5,
  "volume": 0.005,
  "valid_for": "Y",
  "price_lists": [
    { "price_list": 1, "price": 45000.0, "currency": "TZS" },
    { "price_list": 2, "price": 42000.0, "currency": "TZS" }
  ],
  "u_odoo_product_id": 1234
}
```

**Proposed Response:**

```json
{
  "success": true,
  "data": {
    "item_code": "LM-1234",
    "item_name": "Shell Helix HX7 10W-40 5L",
    "items_group_code": 100,
    "action": "created"
  }
}
```

### Odoo Side (New)

| Component | Purpose |
|-----------|---------|
| Automated action on `product.product` | Trigger sync on create/write |
| Job transform `_build_item_dto()` | Map Odoo fields → middleware payload |
| Write-back handler | Confirm `x_sap_item_code` after creation |
| Category mapping model | Map `categ_id` → `ItmsGrpCod` |
| UoM mapping model | Map `uom_id` → SAP UoM codes |
| Tax group mapping | Map `taxes_id` → `VatGourpSa` / `VatGourpPu` |

### SAP DI API Implementation (New)

```csharp
// ISapB1Service.cs
Task<SapItemResponse> CreateItemAsync(SapItemRequest request);
Task<SapItemResponse> UpdateItemAsync(string itemCode, SapItemRequest request);
```

Uses SAP DI API: `GetBusinessObject(BoObjectTypes.oItems)` → set fields → `Add()` / `Update()`

---

## 9. Type Mapping Reference

### Product Type → SAP ItemType

| Odoo `type` | SAP `ItemType` | Description |
|-------------|---------------|-------------|
| `product` | `I` (itItems) | Storable / inventory item |
| `consu` | `I` (itItems) | Consumable (no stock tracking in Odoo, but SAP tracks) |
| `service` | `L` (itLabor) | Service / labour item |

### UoM Mapping (Common)

| Odoo UoM | SAP UoM Code | Notes |
|----------|-------------|-------|
| Units | `Manual` or `Pcs` | Default |
| Litre(s) | `Litre` | Volume-based |
| kg | `kg` | Weight-based |
| Pack | `Pack` | Custom UoM |

> UoM codes must be pre-defined in SAP (Administration → Setup → Inventory → Units of Measurement). The mapping should be configurable.

---

## 10. Current Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                        PRODUCT DATA FLOWS                           │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌─────────┐       ┌──────────┐       ┌─────────────────────┐     │
│  │  SAP B1 │──────>│   Neon   │──────>│        Odoo         │     │
│  │  (OITM) │ Stock │  Cache   │ Sync  │  (product.product)  │     │
│  │         │ Qty   │          │       │                     │     │
│  │         │──────>│ NeonLiqui│──────>│  x_neon_onhand_sap  │     │
│  │         │ Ecom  │ MolyProd │       │  x_ecom_* fields    │     │
│  └─────────┘       └──────────┘       └─────────────────────┘     │
│       ↑                                        │                    │
│       │                                        │ item_code          │
│       │         ┌──────────────┐               │ in SO/INV/RET      │
│       │         │  Middleware  │               │ payloads            │
│       └─────────│  (.NET API)  │<──────────────┘                    │
│     Create SO   │              │                                    │
│     Create INV  │  Resolves    │                                    │
│     Create RET  │  ItemCode    │                                    │
│                 └──────────────┘                                    │
│                                                                     │
│  ═══════════════════════════════════════════════════════════════    │
│  PROPOSED (not yet built):                                          │
│                                                                     │
│  Odoo product.product ──(POST /api/items)──> Middleware ──> SAP B1  │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 11. Summary Table

| Aspect | Current State | Needed for Automation |
|--------|--------------|----------------------|
| Item code on Odoo product | `x_sap_item_code` (Char, unique, indexed) | Already exists |
| Item code on SAP item | `OITM.ItemCode` (primary key) | Already exists |
| Cross-reference | String match on item code | Sufficient |
| Product creation in SAP | Manual only | Middleware endpoint needed |
| Product creation in Odoo | Manual or Neon import | Automated action needed |
| Stock sync | SAP → Neon → Odoo (working) | Already exists |
| Ecom data sync | Neon → Odoo (working) | Already exists |
| Price list sync | Odoo → SAP (export DTO exists) | Partial — needs endpoint |
| Category mapping | None | New mapping model needed |
| UoM mapping | None | New mapping model needed |
| Tax group mapping | None | New mapping model needed |
