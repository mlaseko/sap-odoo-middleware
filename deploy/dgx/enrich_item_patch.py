#!/usr/bin/env python3
"""
Autohub Phase B — DGX classifier patch: /enrich_item (+ helper endpoints).

Idempotently appends the enrichment endpoints to ~/Inventory_Management_Tool/classifier_service.py,
alongside the Phase A /extract_invoice and /extract_parts_invoice endpoints (left untouched).

Run ON the DGX host, then restart the service:
    python3 enrich_item_patch.py
    sudo systemctl restart inventory-classifier

IMPORTANT — this is a SCAFFOLD. The endpoint contracts and the /enrich_item orchestration flow
(§5.1) are complete and match the middleware DTOs, but three data-access helpers must be wired to
your EXISTING Germax-scraper / TecDoc code (the bits that live on the DGX, not in this repo):

    _lookup_germax_row(article, oem_hints)   -> dict | None   (row from neon_germax_products)
    _fetch_tecdoc_full(article, supplier)    -> dict           (categories, cross-refs, vehicles, images)
    _oem_bridge_lookup(oem_numbers)          -> dict | None    (borrowed TecDoc article via oitm_cross_reference)

Each raises NotImplementedError with the exact dict shape it must return, so wiring is mechanical.
Until wired, /enrich_item returns HTTP 501 with a clear message rather than fabricating data.
"""
import os
import sys

TARGET = os.path.expanduser("~/Inventory_Management_Tool/classifier_service.py")

SNIPPET = r'''

# === Autohub Phase B enrichment (added by enrich_item_patch.py) ===
import re as _ah_re

_AH_NOISE = {
    "REAR", "FRONT", "LEFT", "RIGHT", "NON ELECTRICAL",
    "FRONT RIGHT", "FRONT LEFT", "REAR RIGHT", "REAR LEFT",
    "UPPER", "LOWER", "INNER", "OUTER", "PETROL", "DIESEL", "HYBRID",
    "V6", "V8", "V12", "L4", "L6",
}
_AH_ENGINE = _ah_re.compile(r"^\d+(\.\d+)?L$", _ah_re.IGNORECASE)
_AH_OEM = _ah_re.compile(r"^[A-Z0-9][A-Z0-9\-\s]{3,19}$")


def _ah_clean_oems(oems):
    """Option-C noise filter, mirrors the middleware OemFilterService (belt-and-suspenders)."""
    clean, noise = [], []
    for raw in (oems or []):
        t = (raw or "").strip()
        if not t:
            continue
        if t.upper() in _AH_NOISE or _AH_ENGINE.match(t):
            noise.append(t)
        elif _AH_OEM.match(t) and any(c.isdigit() for c in t):
            clean.append(t)
        else:
            noise.append(t)
    return clean, noise


def _ah_is_germax(brand):
    b = (brand or "").upper()
    return "GERMAX" in b or "GAPC" in b


_AH_TECDOC_BRANDS = {"VAG", "BMW", "MB", "MERCEDES", "FORD", "FRD", "MINI", "MIN", "VOLVO", "VOL"}


# ---- Integration points: wire these to your existing Germax/TecDoc code on the DGX ----

def _lookup_germax_row(article, oem_hints):
    """Return a dict (row from neon_germax_products) or None.
    Expected keys used downstream: oem_part_number, primary_description, frgn_name, fit_for_auto,
    image_url, all_image_urls, product_url, tecdoc_categories(list), compatible_vehicles(list)."""
    raise NotImplementedError("Wire _lookup_germax_row to neon_germax_products (see §5.2).")


def _fetch_tecdoc_full(article, supplier):
    """Return the full TecDoc enrichment dict for a known article+supplier (see §5.5)."""
    raise NotImplementedError("Wire _fetch_tecdoc_full to the VAG9999 TecDoc flow (see §5.5).")


def _oem_bridge_lookup(oem_numbers):
    """Return {'article_number','supplier_id','supplier_name','match_via_oem','match_confidence'} or None (§5.4)."""
    raise NotImplementedError("Wire _oem_bridge_lookup to oitm_cross_reference walk (see §5.4).")


def _ah_package_from_germax(row, clean_oems, noise):
    return {
        "enrichment_source": "germax_scraped",
        "borrowed_from": None,
        "confirmation_required": True,
        "item_data": {
            "primary_description": row.get("primary_description"),
            "frgn_name": row.get("frgn_name"),
            "fit_for_auto": row.get("fit_for_auto"),
            "image_url": row.get("image_url"),
            "all_image_urls": row.get("all_image_urls"),
            "product_url": row.get("product_url"),
            "tecdoc_categories": row.get("tecdoc_categories") or [],
            "compatible_vehicles": row.get("compatible_vehicles") or [],
            "filtered_oems": _ah_parse_germax_oems(row.get("oem_part_number")) or clean_oems,
            "suggested_itms_grp_cod": 108,   # Land Rover
            "suggested_sku_prefix": "LR",
        },
        "noise_filtered_tokens": noise,
    }


def _ah_parse_germax_oems(oem_field):
    if not oem_field:
        return []
    parts = _ah_re.split(r"[\\/+,;]", oem_field)
    return [p.strip() for p in parts if p.strip()]


@app.post("/lookup_germax")
async def lookup_germax(request: Request):
    body = await request.json()
    row = _lookup_germax_row(body.get("germax_article_number"), body.get("oem_hints") or [])
    if not row:
        return JSONResponse({"found": False, "match_method": None, "scraped_data": None})
    return JSONResponse({"found": True, "match_method": "germax_article_number_exact", "scraped_data": row})


@app.post("/lookup_tecdoc_oem_bridge")
async def lookup_tecdoc_oem_bridge(request: Request):
    body = await request.json()
    return JSONResponse({"borrowed_article": _oem_bridge_lookup(body.get("oem_numbers") or [])})


@app.post("/fetch_tecdoc_full")
async def fetch_tecdoc_full(request: Request):
    body = await request.json()
    return JSONResponse(_fetch_tecdoc_full(body.get("article_number"), body.get("supplier")))


@app.post("/enrich_item")
async def enrich_item(request: Request):
    body = await request.json()
    brand = body.get("brand")
    article = body.get("supplier_article_number")
    clean, noise = _ah_clean_oems(body.get("oem_numbers") or [])

    try:
        # Option 1 first: Germax scraped data is the source of truth for LR items (§5.1 steps 2-4).
        if _ah_is_germax(brand) or any(o.upper().startswith("LR") for o in clean):
            row = _lookup_germax_row(article, clean)
            if row:
                return JSONResponse(_ah_package_from_germax(row, clean, noise))

        brand_key = (brand or "").upper()
        if brand_key in _AH_TECDOC_BRANDS:
            data = _fetch_tecdoc_full(article, brand)            # TecDoc-direct (auto-accept)
            data.setdefault("filtered_oems", clean)
            return JSONResponse({
                "enrichment_source": "tecdoc_direct", "borrowed_from": None,
                "confirmation_required": False, "item_data": data, "noise_filtered_tokens": noise,
            })

        # Non-TecDoc brand: borrow a TecDoc article via OEM bridge, then fetch full (confirm required).
        borrowed = _oem_bridge_lookup(clean)
        if borrowed:
            data = _fetch_tecdoc_full(borrowed["article_number"], borrowed.get("supplier_name"))
            data.setdefault("filtered_oems", clean)
            return JSONResponse({
                "enrichment_source": "borrowed_oem_bridge", "borrowed_from": borrowed,
                "confirmation_required": True, "item_data": data, "noise_filtered_tokens": noise,
            })

        return JSONResponse({
            "enrichment_source": "not_found", "borrowed_from": None,
            "confirmation_required": True, "item_data": None, "noise_filtered_tokens": noise,
        })
    except NotImplementedError as e:
        return JSONResponse(
            {"error": "enrichment integration not wired", "detail": str(e)}, status_code=501)
# === end Autohub Phase B enrichment ===
'''


def main() -> int:
    if not os.path.exists(TARGET):
        print(f"ERROR: {TARGET} not found. Adjust the path and re-run.", file=sys.stderr)
        return 1
    with open(TARGET, "r", encoding="utf-8") as f:
        src = f.read()
    if "/enrich_item" in src:
        print("Already patched — /enrich_item present. No changes made.")
        return 0
    for needed in ("@app.post", "Request", "JSONResponse"):
        if needed not in src:
            print(f"ERROR: '{needed}' not found in {TARGET}; refusing to patch an unexpected file.", file=sys.stderr)
            return 2
    with open(TARGET, "a", encoding="utf-8") as f:
        f.write(SNIPPET)
    print(f"Patched: {TARGET}")
    print("Wire the three _lookup_* helpers, then restart inventory-classifier.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
