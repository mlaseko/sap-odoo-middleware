#!/usr/bin/env python3
"""
Autohub Phase B — DGX classifier patch: /enrich_item + helper endpoints (Slice 1).

Idempotently appends the enrichment endpoints to ~/Inventory_Management_Tool/classifier_service.py,
alongside the Phase A /extract_invoice and /extract_parts_invoice endpoints (left untouched).

Run ON the DGX host, then restart the service:
    python3 enrich_item_patch.py
    sudo systemctl restart inventory-classifier

Slice 1 scope (per the enrichment spec + Mohamed's decisions):
    * Path A  — TecDoc direct        (auto-confirmed)
    * Path C  — borrowed via OEM bridge (confirmation required; FIRST hit wins — Q10)
    * Path B  — Germax scrape is DEFERRED to Slice 2 (no /lookup_germax, /scrape_germax here)
    * Image verification is OFF by default (Q5) — middleware sends skip_image_verify=true
    * DGX auth: Tailscale-only (Q7) — no bearer token

Endpoints added: /enrich_item, /lookup_tecdoc_oem_bridge, /fetch_tecdoc_full, /verify_image_visu.

IMPORTANT — this is a SCAFFOLD. The orchestration + response contract are complete and match the
middleware DTOs, but FOUR data-access helpers must be wired to the EXISTING TecDoc/DB code that
lives on the DGX (not in this repo). Each raises NotImplementedError documenting the exact dict
shape it must return, so wiring is mechanical:

    _tecdoc_fetch_full(tecdoc_article_id?, article_number?, supplier_id?) -> dict   (the VAG9999 TecDoc flow)
    _oem_bridge_candidates(oem_number, brand_hint, max_results)          -> list    (oitm_cross_reference walk)
    _upsert_oitm(td, brand, oems, source, borrowed?)                     -> int     (parts_catalog.oitm row, item_code NULL)
    # _verify_image is implemented here (HEAD-only); swap in your vision check if desired.

TODO(mohamed): point _tecdoc_fetch_full / _oem_bridge_candidates at the real RapidAPI TecDoc client
and the oitm_cross_reference mirror once you can see their module names on the box. Until wired,
/enrich_item returns HTTP 501 (integration_not_wired) rather than fabricating data.
"""
import os
import sys

TARGET = os.path.expanduser("~/Inventory_Management_Tool/classifier_service.py")

SNIPPET = r'''

# === Autohub Phase B enrichment — Slice 1 (added by enrich_item_patch.py) ===
import re as _ah_re
import time as _ah_time
import uuid as _ah_uuid
import urllib.request as _ah_url

# In-process idempotency cache: request_id -> (expiry_epoch, response_dict). 5-minute TTL.
_AH_CACHE = {}
_AH_CACHE_TTL = 300


def _ah_cache_get(req_id):
    hit = _AH_CACHE.get(req_id)
    if not hit:
        return None
    if hit[0] < _ah_time.time():
        _AH_CACHE.pop(req_id, None)
        return None
    return hit[1]


def _ah_cache_put(req_id, resp):
    _AH_CACHE[req_id] = (_ah_time.time() + _AH_CACHE_TTL, resp)
    return resp


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


# brand -> (sku_prefix, itms_grp_cod, vehicle_category).
# TODO(mohamed): confirm the itms_grp_cod values against SAP OITB (these are best-guess group codes).
_AH_BRAND_HINTS = {
    "VAG": ("VAG", 105, "VAG"), "AUDI": ("VAG", 105, "VAG"), "VW": ("VAG", 105, "VAG"),
    "VOLKSWAGEN": ("VAG", 105, "VAG"), "SKODA": ("VAG", 105, "VAG"), "SEAT": ("VAG", 105, "VAG"),
    "BMW": ("BM", 105, "BMW"), "MINI": ("MINI", 105, "BMW"),
    "MB": ("MB", 106, "MB"), "MERCEDES": ("MB", 106, "MB"), "MERCEDES-BENZ": ("MB", 106, "MB"),
    "FORD": ("FRD", 107, "FORD"), "VOLVO": ("VOL", 107, "VOLVO"),
    "LR": ("LR", 108, "LR"), "LAND ROVER": ("LR", 108, "LR"), "GERMAX": ("LR", 108, "LR"),
}


def _ah_sap_hints(brand, oems):
    b = (brand or "").upper().strip()
    pref = _AH_BRAND_HINTS.get(b)
    if not pref and any((o or "").upper().startswith("LR") for o in oems):
        pref = _AH_BRAND_HINTS["LR"]
    if not pref:
        pref = ("GEN", 105, "VAG")
    return {"suggested_prefix": pref[0], "suggested_itms_grp_cod": pref[1], "vehicle_category": pref[2]}


def _ah_looks_like_tecdoc_article(article):
    """Permissive heuristic: a non-trivial alphanumeric code. The real TecDoc fetch returns
    found=false for misses, so a false positive just falls through to the OEM bridge."""
    a = (article or "").strip()
    return len(a) >= 4 and any(c.isdigit() for c in a) and any(c.isalpha() for c in a)


# ---- TODO integration points: wire to the existing DGX TecDoc / parts_catalog code ----

def _tecdoc_fetch_full(tecdoc_article_id=None, article_number=None, supplier_id=None):
    """Return the full TecDoc record, or {'found': False}. Expected keys when found:
       article_number, supplier_name, supplier_id, tecdoc_article_id, description, fit_for_auto,
       image_url, all_image_urls(list), all_oems(list), product_url, frgn_name,
       tecdoc_categories(list), compatible_vehicles(list), specs(dict)."""
    raise NotImplementedError(
        "Wire _tecdoc_fetch_full to the RapidAPI TecDoc client (the VAG9999 flow).")


def _oem_bridge_candidates(oem_number, brand_hint=None, max_results=5):
    """Return a ranked list (best first) of donor candidates from oitm_cross_reference, or [].
       Each: {donor_tecdoc_article_id, donor_article_number, donor_supplier_name, donor_supplier_id,
              matched_via_oem, confidence}. Filter reference_type='oem'; rank OEM>Mann>Mahle>Vaico>…"""
    raise NotImplementedError(
        "Wire _oem_bridge_candidates to the oitm_cross_reference walk (reference_type='oem').")


def _upsert_oitm(td, brand, oems, source, borrowed=None):
    """Create-or-locate the parts_catalog.oitm row (item_code NULL until NeonBridge sets it) and the
       oitm_cross_reference rows for each OEM. Return its integer id (neon_oitm_id). Idempotent on
       (article_number, supplier_name, source)."""
    raise NotImplementedError(
        "Wire _upsert_oitm to the parts_catalog.oitm/oitm_cross_reference upsert; return neon_oitm_id.")


def _verify_image(url):
    """HEAD-only reachability/placeholder check (Q5: vision check off in Slice 1)."""
    try:
        req = _ah_url.Request(url, method="HEAD")
        with _ah_url.urlopen(req, timeout=5) as r:
            ctype = r.headers.get("Content-Type", "") or ""
            clen = int(r.headers.get("Content-Length") or 0)
            return {"ok": ctype.startswith("image/") and clen >= 2048, "url_reachable": True,
                    "mime_type": ctype, "size_bytes": clen,
                    "looks_like_placeholder": 0 < clen < 2048, "vision_check_used": False}
    except Exception as e:                                  # noqa: BLE001
        return {"ok": False, "url_reachable": False, "mime_type": None, "size_bytes": 0,
                "looks_like_placeholder": False, "vision_check_used": False, "error": str(e)}


# ---- response builders (emit BOTH the spec block and the middleware-compat aliases) ----

def _ah_success(source, td, borrowed, hints, neon_id, conf_req, clean, noise, audit, t0, req_id):
    audit = dict(audit)
    audit["duration_ms"] = int((_ah_time.time() - t0) * 1000)
    enrichment = {                                          # spec §3 block
        "article_number": td.get("article_number"),
        "supplier_name": td.get("supplier_name"),
        "supplier_id": td.get("supplier_id"),
        "tecdoc_article_id": td.get("tecdoc_article_id"),
        "description": td.get("description"),
        "fit_for_auto": td.get("fit_for_auto"),
        "image_url": td.get("image_url"),
        "image_verified": td.get("image_verified", False),
        "all_image_urls": td.get("all_image_urls") or [],
        "all_oems_found": td.get("all_oems") or clean,
        "specs": td.get("specs") or {},
    }
    item_data = {                                           # middleware EnrichmentItemData keys
        "primary_description": td.get("description"),
        "frgn_name": td.get("frgn_name"),
        "fit_for_auto": td.get("fit_for_auto"),
        "image_url": td.get("image_url"),
        "all_image_urls": (",".join(td.get("all_image_urls") or []) or None),
        "product_url": td.get("product_url"),
        "tecdoc_categories": td.get("tecdoc_categories") or [],
        "compatible_vehicles": td.get("compatible_vehicles") or [],
        "filtered_oems": clean,
        "suggested_itms_grp_cod": hints["suggested_itms_grp_cod"],
        "suggested_sku_prefix": hints["suggested_prefix"],
    }
    borrowed_from = None
    if borrowed:
        sid = borrowed.get("donor_supplier_id")
        borrowed_from = {                                   # middleware BorrowedFrom keys
            "article_number": borrowed.get("donor_article_number"),
            "supplier_id": (str(sid) if sid is not None else None),
            "supplier_name": borrowed.get("donor_supplier"),
            "match_via_oem": borrowed.get("matched_via_oem"),
            "match_confidence": None,
        }
    return {
        "request_id": req_id, "status": "success", "source": source,
        "enrichment_source": source, "confirmation_required": bool(conf_req),
        "neon_oitm_id": neon_id,
        "enrichment": enrichment, "item_data": item_data,
        "borrowed": borrowed, "borrowed_from": borrowed_from,
        "sap_hints": hints, "noise_filtered_tokens": noise, "audit": audit,
    }


def _ah_partial(req_id, brand, article, clean, hints, noise, audit, t0):
    audit = dict(audit)
    audit["duration_ms"] = int((_ah_time.time() - t0) * 1000)
    return {
        "request_id": req_id, "status": "partial", "source": "unmatched",
        "enrichment_source": "unmatched", "confirmation_required": True,
        "neon_oitm_id": None, "enrichment": None, "item_data": None,
        "borrowed": None, "borrowed_from": None,
        "reason": "no_tecdoc_match_no_oem_bridge",
        "available_hints": {"extracted_brand": brand, "extracted_article": article, "oems_searched": clean},
        "sap_hints": hints, "noise_filtered_tokens": noise, "audit": audit,
    }


@app.post("/verify_image_visu")
async def verify_image_visu(request: Request):
    body = await request.json()
    url = body.get("image_url")
    if not url:
        return JSONResponse({"ok": False, "url_reachable": False, "error": "image_url required"}, status_code=400)
    return JSONResponse(_verify_image(url))


@app.post("/fetch_tecdoc_full")
async def fetch_tecdoc_full(request: Request):
    body = await request.json()
    try:
        td = _tecdoc_fetch_full(
            tecdoc_article_id=body.get("tecdoc_article_id"),
            article_number=body.get("article_number"),
            supplier_id=body.get("supplier_id"))
        return JSONResponse(td)
    except NotImplementedError as e:
        return JSONResponse({"found": False, "error": "integration_not_wired", "detail": str(e)}, status_code=501)


@app.post("/lookup_tecdoc_oem_bridge")
async def lookup_tecdoc_oem_bridge(request: Request):
    body = await request.json()
    try:
        cands = _oem_bridge_candidates(
            body.get("oem_number"), body.get("brand_hint"), int(body.get("max_results") or 5))
        return JSONResponse({"found": bool(cands), "candidates": cands or []})
    except NotImplementedError as e:
        return JSONResponse({"found": False, "candidates": [], "error": "integration_not_wired", "detail": str(e)},
                            status_code=501)


@app.post("/enrich_item")
async def enrich_item(request: Request):
    body = await request.json()
    req_id = body.get("request_id") or ("req-" + _ah_uuid.uuid4().hex)

    cached = _ah_cache_get(req_id)
    if cached is not None:
        return JSONResponse(cached)

    # Tolerate both the spec's nested {extracted:{...}} and the middleware's current flat shape.
    extracted = body.get("extracted") or {}
    brand = extracted.get("brand") or body.get("brand")
    article = extracted.get("article") or body.get("supplier_article_number")
    raw_oems = extracted.get("oems") or body.get("oem_numbers") or []
    options = body.get("options") or {}
    skip_img = bool(options.get("skip_image_verify", True))
    max_bridges = int(options.get("max_oem_bridges_to_try", 5))

    clean, noise = _ah_clean_oems(raw_oems)
    hints = _ah_sap_hints(brand, clean)
    t0 = _ah_time.time()
    audit = {"tecdoc_calls": 0, "germax_scrape_calls": 0, "oem_bridges_tried": 0}

    try:
        # Path A — TecDoc direct (auto-confirmed).
        if _ah_looks_like_tecdoc_article(article):
            audit["tecdoc_calls"] += 1
            td = _tecdoc_fetch_full(article_number=article)
            if td and td.get("found", True):
                if not skip_img and td.get("image_url"):
                    td["image_verified"] = bool(_verify_image(td["image_url"]).get("ok"))
                neon_id = _upsert_oitm(td, brand, clean, source="tecdoc_direct")
                resp = _ah_success("tecdoc_direct", td, None, hints, neon_id, False,
                                   clean, noise, audit, t0, req_id)
                return JSONResponse(_ah_cache_put(req_id, resp))

        # Path C — borrowed via OEM bridge (first hit wins — Q10), confirmation required.
        for oem in clean[:max_bridges]:
            audit["oem_bridges_tried"] += 1
            cands = _oem_bridge_candidates(oem, brand, max_bridges)
            if cands:
                donor = cands[0]
                audit["tecdoc_calls"] += 1
                td = _tecdoc_fetch_full(
                    tecdoc_article_id=donor.get("donor_tecdoc_article_id"),
                    article_number=donor.get("donor_article_number"),
                    supplier_id=donor.get("donor_supplier_id"))
                if td and td.get("found", True):
                    if not skip_img and td.get("image_url"):
                        td["image_verified"] = bool(_verify_image(td["image_url"]).get("ok"))
                    borrowed = {
                        "donor_article_number": donor.get("donor_article_number"),
                        "donor_supplier": donor.get("donor_supplier_name"),
                        "donor_tecdoc_article_id": donor.get("donor_tecdoc_article_id"),
                        "donor_supplier_id": donor.get("donor_supplier_id"),
                        "matched_via_oem": oem,
                        "confidence": donor.get("confidence", "high"),
                        "explanation": "Donor article %s shares OEM %s with this line."
                                       % (donor.get("donor_article_number"), oem),
                    }
                    neon_id = _upsert_oitm(td, brand, clean, source="borrowed_oem_bridge", borrowed=borrowed)
                    audit["oem_bridge_winner"] = oem
                    resp = _ah_success("borrowed_oem_bridge", td, borrowed, hints, neon_id, True,
                                       clean, noise, audit, t0, req_id)
                    return JSONResponse(_ah_cache_put(req_id, resp))

        # Nothing matched — partial (middleware moves the line to needs_manual).
        resp = _ah_partial(req_id, brand, article, clean, hints, noise, audit, t0)
        return JSONResponse(_ah_cache_put(req_id, resp))

    except NotImplementedError as e:
        return JSONResponse({
            "request_id": req_id, "status": "failed", "source": None, "enrichment_source": None,
            "confirmation_required": False, "neon_oitm_id": None, "item_data": None,
            "error": {"code": "integration_not_wired", "message": str(e), "retryable": False},
            "audit": audit,
        }, status_code=501)
    except Exception as e:                                  # noqa: BLE001
        # Soft-fail with 200 so the middleware reads status=failed and routes the line to needs_manual.
        return JSONResponse({
            "request_id": req_id, "status": "failed", "source": None, "enrichment_source": None,
            "confirmation_required": False, "neon_oitm_id": None, "item_data": None,
            "error": {"code": "enrich_internal_error", "message": str(e), "retryable": True},
            "audit": audit,
        })
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
    print("Wire _tecdoc_fetch_full / _oem_bridge_candidates / _upsert_oitm, then restart inventory-classifier.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
