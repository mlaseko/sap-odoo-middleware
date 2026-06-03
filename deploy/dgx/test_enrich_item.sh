#!/usr/bin/env bash
# Smoke test for the Autohub /enrich_item endpoint (Slice 1). Run from anywhere on the Tailnet:
#     ./test_enrich_item.sh                 # uses http://spark-09cc:8077
#     DGX=http://localhost:8077 ./test_enrich_item.sh
#
# Covers: Path A (TecDoc-direct), Path C (borrowed OEM bridge), and a partial/unmatched response.
# Until the TecDoc/oitm helpers are wired, the first two return HTTP 501 (integration_not_wired) —
# that itself confirms the routing/contract is live; the partial case returns 200 immediately.
set -u
DGX="${DGX:-http://spark-09cc:8077}"
H='Content-Type: application/json'
pretty() { python3 -m json.tool 2>/dev/null || cat; }

echo "=== DGX: $DGX ==="

echo
echo "### 1) Path A — TecDoc direct (VAG article) — expect source=tecdoc_direct, confirmation_required=false"
curl -s -X POST "$DGX/enrich_item" -H "$H" -d '{
  "request_id": "req-smoke-A",
  "document_id": "doc-smoke",
  "line_id": "line-A",
  "supplier_brand_invoice": "TANTIVY AUTOMOTIVE",
  "extracted": {
    "brand": "VAG", "article": "06J109259A",
    "oems": ["06J109259A", "06L109259A"],
    "description": "Timing chain tensioner", "qty": 1,
    "unit_price_invoice_ccy": 35.50, "invoice_currency": "USD"
  },
  "options": { "scrape_if_missing": false, "max_oem_bridges_to_try": 5, "skip_image_verify": true }
}' | pretty

echo
echo "### 2) Path C — borrowed via OEM bridge (non-TecDoc brand) — expect source=borrowed_oem_bridge, confirmation_required=true"
curl -s -X POST "$DGX/enrich_item" -H "$H" -d '{
  "request_id": "req-smoke-C",
  "document_id": "doc-smoke",
  "line_id": "line-C",
  "supplier_brand_invoice": "TANTIVY AUTOMOTIVE",
  "extracted": {
    "brand": "OE", "article": "8R0853651A/1QP",
    "oems": ["8R0853651A"],
    "description": "Front grille", "qty": 1,
    "unit_price_invoice_ccy": 245.00, "invoice_currency": "USD"
  },
  "options": { "scrape_if_missing": false, "max_oem_bridges_to_try": 5, "skip_image_verify": true }
}' | pretty

echo
echo "### 3) Partial — no article, junk OEM — expect status=partial, source=unmatched, item_data=null"
curl -s -X POST "$DGX/enrich_item" -H "$H" -d '{
  "request_id": "req-smoke-partial",
  "document_id": "doc-smoke",
  "line_id": "line-P",
  "supplier_brand_invoice": "TANTIVY AUTOMOTIVE",
  "extracted": {
    "brand": "OE", "article": "",
    "oems": ["FRONT", "REAR"],
    "description": "Mystery part", "qty": 1,
    "unit_price_invoice_ccy": 10.00, "invoice_currency": "USD"
  },
  "options": { "scrape_if_missing": false, "max_oem_bridges_to_try": 5, "skip_image_verify": true }
}' | pretty

echo
echo "### 4) Helper — /verify_image_visu (HEAD check, implemented; no wiring needed)"
curl -s -X POST "$DGX/verify_image_visu" -H "$H" -d '{"image_url":"https://www.tecdoc.de/images/552871.jpg","context":"auto_part"}' | pretty

echo
echo "Done. Paths 1 & 2 show 501 integration_not_wired until _tecdoc_fetch_full / _oem_bridge_candidates / _upsert_oitm are wired."
