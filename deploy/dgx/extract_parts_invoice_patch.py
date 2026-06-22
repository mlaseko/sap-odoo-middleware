#!/usr/bin/env python3
"""
Autohub Phase A — DGX classifier patch.

Idempotently adds the /extract_parts_invoice endpoint (spare-parts prompt) to
~/Inventory_Management_Tool/classifier_service.py, alongside the existing /extract_invoice
endpoint used by Lubes (which is left untouched). Same model, num_ctx, and JSON-format constraint.

Run ON the spark-09cc (DGX) host, then restart the service:
    python3 extract_parts_invoice_patch.py
    sudo systemctl restart inventory-classifier   # or however the service is managed

Re-running is safe: if the endpoint is already present, nothing changes.
"""
import os
import sys

TARGET = os.path.expanduser("~/Inventory_Management_Tool/classifier_service.py")

SNIPPET = r'''

# === Autohub spare-parts extraction (added by autohub-phase-a) ===
PARTS_INVOICE_PROMPT = """You are extracting structured data from a SPARE PARTS supplier invoice (PDF page rendered as image). The invoice may use any of: USD, AED, GBP, EUR. The supplier may be Chinese (Tantivy/VIKA), Land Rover specialist (Germax), German (Liqui Moly), or others. Layouts vary widely.

Return ONLY valid JSON in this exact shape (no preamble, no markdown):

{
  "header": {
    "supplier_name": string | null,        // who is selling to us (NOT "MOLAS"; that's us)
    "invoice_number": string | null,
    "invoice_date": "YYYY-MM-DD" | null,
    "currency": "USD" | "AED" | "GBP" | "EUR" | null,
    "total_amount": number | null          // grand total in invoice currency
  },
  "lines": [
    {
      "supplier_article_number": string | null,   // the supplier's own SKU (e.g. "G2261", "GL0010", "B1044503", "11360101")
      "oem_numbers": [string],                     // ALL OEM/cross-reference numbers visible on this line, as an array
      "description": string | null,                // part type/name (e.g. "Coolant pipe; rubber", "Brake Pads")
      "brand": string | null,                      // brand visible on the line (e.g. "vika", "DPA", "Borsehung", "Monroe")
      "quantity": number | null,
      "unit": string | null,                       // "pcs", "set", "Piece", etc., as written on the invoice
      "unit_price_foreign": number | null,         // unit price in invoice currency (USD/AED/GBP/EUR)
      "discount_pct": number | null,
      "line_total_foreign": number | null          // line total in invoice currency
    }
  ]
}

EXTRACTION RULES:

- The "supplier_name" is the COMPANY SELLING TO US. Look at the top of the invoice for the seller's identity (Tantivy Automotive, Germax/GAPC, etc.). Do NOT use "Molas Solutions" — that's the buyer.

- "oem_numbers" is ALWAYS an array, even with one element. Multiple OEMs in a single cell may appear with separators:
    "06J109259A/06L109259A"  -> ["06J109259A", "06L109259A"]
    "17138610662+17117639020" -> ["17138610662", "17117639020"]
    "8R0853651A 1QP/8R0853651 1QP" -> ["8R0853651A", "8R0853651 1QP"]   (preserve internal spaces)
    Be tolerant of "/" and "+" as separators.

- "supplier_article_number" is the SUPPLIER's own SKU on this line. Common columns to look for: "TAN No.", "Model Number", "Item No.", "Code". This is DIFFERENT from OEMs. If the line shows only an OEM in one column and nothing else, supplier_article_number is null.

- "brand" can vary per-line on the same invoice (a Chinese parts supplier may sell vika, DPA, Borsehung mixed). Extract the brand as printed, preserving case as best you can (e.g., "vika" not "VIKA" if lowercase on invoice).

- "currency": determine from explicit symbols ($, EUR, GBP, AED markers, USD, EUR, GBP) in the invoice header or column header. Default to "USD" only if you see $ symbols and no other currency markers. Return null if you cannot determine.

- "invoice_date": handle BOTH US (MM/DD/YYYY) AND European (DD/MM/YYYY) AND date-month-year (e.g., "25-May-26") formats. Output as YYYY-MM-DD. When ambiguous between US and European, prefer DD/MM/YYYY for non-US suppliers (Chinese, German, etc.).

- "total_amount" appears on the LAST page only. Pages 1 through N-1 should return total_amount as null. Do NOT invent or guess this value.

- Number parsing: handle US ("1,068.00"), European ("1.068,00"), and plain ("204") formats. Normalize to JSON numbers without thousands separators.

- LINE-level NUMERIC fields (quantity, unit_price_foreign, discount_pct, line_total_foreign) must be JSON numbers, never strings, never null. If not visible, use 0.

- discount_pct: default 0 when no discount column shows a value. "100 %" -> 100.

- $0.00 lines: some invoices include promotional items at $0.00 (mugs, hoodies, posters, samples). Extract them normally — set is_promotional false in the JSON (your responsibility ends at extraction; the worker decides what's promotional based on rules).

- Promotional content hints in the description (e.g., "Borsehung mug", "Gray hoodie", "Borsehung Poster"): include them as regular lines; do NOT skip them.

- "supplier_article_number" should preserve any prefix letters: "B1044503" stays "B1044503", "GL0010" stays "GL0010". Do not strip prefixes.

- Multi-page invoices: each page's lines are separate. Do NOT carry over line numbers; just emit what you see on this page.

- If a column is genuinely empty or unreadable, return null for that string field. For numeric fields, follow the rules above (0 for line numerics, null for total_amount).

This invoice page is provided as an image. Extract every visible line. If the page shows ONLY header info (cover page) with no line items, return an empty "lines" array."""


@app.post("/extract_parts_invoice")
async def extract_parts_invoice(request: Request):
    payload = await request.json()
    image_b64 = payload.get("image_base64")
    page_no = payload.get("page_no", 1)
    if not image_b64:
        return JSONResponse({"error": "image_base64 required"}, status_code=422)

    r = requests.post(
        f"{OLLAMA}/api/chat",
        timeout=600,
        json={
            "model": VISION_MODEL,
            "format": "json",
            "stream": False,
            "options": {"temperature": 0, "num_ctx": 16384},
            "messages": [{
                "role": "user",
                "content": PARTS_INVOICE_PROMPT,
                "images": [image_b64],
            }],
        },
    )
    r.raise_for_status()
    content = r.json().get("message", {}).get("content", "")
    return JSONResponse(json.loads(content))
# === end autohub-phase-a ===
'''


def main() -> int:
    if not os.path.exists(TARGET):
        print(f"ERROR: {TARGET} not found. Adjust the path and re-run.", file=sys.stderr)
        return 1

    with open(TARGET, "r", encoding="utf-8") as f:
        src = f.read()

    if "/extract_parts_invoice" in src:
        print("Already patched — /extract_parts_invoice present. No changes made.")
        return 0

    # Light sanity check that we're appending into the expected module.
    for needed in ("@app.post", "OLLAMA", "VISION_MODEL"):
        if needed not in src:
            print(f"ERROR: '{needed}' not found in {TARGET}; refusing to patch an unexpected file.", file=sys.stderr)
            return 2

    with open(TARGET, "a", encoding="utf-8") as f:
        f.write(SNIPPET)

    print(f"Patched: {TARGET}")
    print("Now restart the service (e.g. `sudo systemctl restart inventory-classifier`).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
