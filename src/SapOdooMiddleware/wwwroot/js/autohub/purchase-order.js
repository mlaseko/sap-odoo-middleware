// Purchase order card on the Create & finish tab (/autohub/documents/{id}/finish): once the invoice is
// reviewed, pick the SAP vendor (remembered per supplier), check the preview (lines at PL01 in TZS, warehouse,
// lines left off, total) and create the purchase order in SAP. One PO per invoice.
(function () {
    const root = document.getElementById('po-root');
    if (!root) return;
    const docId = root.dataset.docId;
    const API_KEY = document.querySelector('meta[name="api-key"]')?.content || '';
    const esc = s => (s == null ? '' : String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])));
    const money = n => Number(n || 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    let preview = null;
    let cardCode = null;      // the vendor chosen in this visit (null = the remembered one)
    let busy = false;

    async function api(method, path, payload) {
        const opts = { method, headers: {} };
        if (API_KEY) opts.headers['X-Api-Key'] = API_KEY;
        if (payload !== undefined) { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(payload); }
        const r = await fetch('/api/autohub' + path, opts);
        const text = await r.text();
        const json = text ? JSON.parse(text) : null;
        if (!r.ok) {
            const err = new Error((json && json.error) || ('HTTP ' + r.status));
            err.errors = json && json.errors;
            throw err;
        }
        return json;
    }

    async function load() {
        const q = cardCode ? '?card_code=' + encodeURIComponent(cardCode) : '';
        try { preview = await api('GET', '/documents/' + docId + '/purchase-order' + q); }
        catch (e) { root.innerHTML = '<div class="fi-card"><h3>Purchase order</h3><div class="note-error">Could not load: ' + esc(e.message) + '</div></div>'; return; }
        render();
    }

    function render(message, kind) {
        const p = preview;
        let html = '<div class="fi-card' + (p.purchase_order ? '' : (p.ready ? ' go' : '')) + '"><h3>Purchase order</h3>';
        if (message) html += '<div class="' + (kind === 'error' ? 'note-error' : 'notice') + '">' + message + '</div>';

        if (p.purchase_order) {
            const po = p.purchase_order;
            html += '<p class="fi-done"><strong>Purchase order ' + po.doc_num + '</strong> created in SAP for ' + esc(po.card_code) +
                (po.line_count ? ': ' + po.line_count + ' line(s), ' + money(po.doc_total) + ' TZS' : '') +
                ' (by ' + esc(po.created_by) + ').</p></div>';
            root.innerHTML = html;
            return;
        }
        if (p.document_status !== 'reviewed') {
            html += '<p class="sub">Available once the review is complete. The PO is created in TZS at PL01 cost, ' +
                'every line to warehouse ' + esc(p.warehouse) + '.</p></div>';
            root.innerHTML = html;
            return;
        }

        // Vendor
        html += '<div class="sub">Invoice ' + esc(p.invoice_number || '—') + ' · supplier ' + esc(p.supplier_name || '—') +
            (p.invoice_total != null ? ' · invoice total ' + money(p.invoice_total) + ' ' + esc(p.invoice_currency || '') : '') + '</div>';
        html += '<div class="fi-actions"><label>SAP vendor: <strong>' +
            (p.vendor ? esc(p.vendor.card_code) + ' · ' + esc(p.vendor.card_name || '') + (p.vendor.remembered ? ' <span class="sub">(remembered for this supplier)</span>' : '') : '<em>not chosen</em>') +
            '</strong></label></div>';
        html += '<div class="fi-actions"><input type="search" id="po-vendor-search" placeholder="Search vendor code or name" style="min-width:260px">' +
            '<button type="button" class="btn btn-sm" id="po-vendor-find">Find</button><select id="po-vendor-list" style="display:none; min-width:260px"></select></div>';

        // Lines
        html += '<p class="sub">' + p.lines.length + ' line(s) to warehouse ' + esc(p.warehouse) + ', in ' + esc(p.currency) +
            ' at PL01 cost · total <strong>' + money(p.doc_total) + ' ' + esc(p.currency) + '</strong>';
        const calculated = p.lines.filter(l => l.price_source === 'calculated').length;
        if (calculated) html += ' · ' + calculated + ' matched line(s) costed from this invoice';
        html += '</p>';
        if (p.excluded.length)
            html += '<p class="sub">Left off: ' + p.excluded.length + ' line(s) (' +
                Object.entries(p.excluded.reduce((a, x) => (a[x.reason] = (a[x.reason] || 0) + 1, a), {}))
                    .map(([r, n]) => n + ' ' + esc(r)).join(', ') + ').</p>';
        if (p.lines.length)
            html += '<details><summary>Show the lines</summary><table class="table"><thead><tr><th>#</th><th>Item</th><th>Description</th>' +
                '<th style="text-align:right">Qty</th><th style="text-align:right">PL01 (TZS)</th><th style="text-align:right">Total</th></tr></thead><tbody>' +
                p.lines.map(l => '<tr><td>' + l.line_number + '</td><td>' + esc(l.item_code) + '</td><td>' + esc(l.description || '') + '</td>' +
                    '<td style="text-align:right">' + l.quantity + '</td><td style="text-align:right">' + money(l.unit_price) +
                    (l.price_source === 'calculated' ? ' <span class="sub" title="Matched part: PL01 worked out from this invoice">*</span>' : '') +
                    '</td><td style="text-align:right">' + money(l.line_total) + '</td></tr>').join('') +
                '</tbody></table></details>';

        if (p.blocking.length)
            html += '<p class="sub">Before the purchase order can be created:</p><ul>' + p.blocking.map(b => '<li>' + esc(b) + '</li>').join('') + '</ul>';
        html += '<div class="fi-actions"><button type="button" class="btn btn-primary" id="po-create"' + (p.ready && !busy ? '' : ' disabled') + '>' +
            (busy ? 'Creating…' : 'Create purchase order in SAP') + '</button></div></div>';
        root.innerHTML = html;
    }

    async function findVendors() {
        const term = document.getElementById('po-vendor-search').value.trim();
        const list = document.getElementById('po-vendor-list');
        let vendors;
        try { vendors = await api('GET', '/vendors?search=' + encodeURIComponent(term) + '&limit=50'); }
        catch (e) { render('Could not search vendors: ' + esc(e.message), 'error'); return; }
        list.innerHTML = '<option value="">' + (vendors.length ? 'Choose a vendor (' + vendors.length + ')' : 'No vendor found') + '</option>' +
            vendors.map(v => '<option value="' + esc(v.card_code) + '">' + esc(v.card_code) + ' · ' + esc(v.card_name || '') +
                (v.currency ? ' (' + esc(v.currency) + ')' : '') + '</option>').join('');
        list.style.display = '';
    }

    async function create() {
        const p = preview;
        if (!await window.ahConfirm({
            title: 'Create the purchase order in SAP?',
            rows: [
                ['Vendor', p.vendor.card_code + ' · ' + (p.vendor.card_name || '')],
                ['Lines', p.lines.length + ' (warehouse ' + p.warehouse + ')'],
                ['Total', money(p.doc_total) + ' ' + p.currency],
                ['Invoice', p.invoice_number || '—'],
            ],
            note: 'This cannot be undone from here.',
            ok: 'Create purchase order',
        })) return;
        busy = true; render();
        try {
            const r = await api('POST', '/documents/' + docId + '/purchase-order', { card_code: p.vendor.card_code });
            busy = false;
            await load();
            if (r.already_existed) render('This invoice already had purchase order ' + r.purchase_order.doc_num + '.');
        } catch (e) {
            busy = false;
            const detail = e.errors && e.errors.length > 1 ? '<ul>' + e.errors.map(x => '<li>' + esc(x) + '</li>').join('') + '</ul>' : esc(e.message);
            render('The purchase order was not created: ' + detail, 'error');
        }
    }

    root.addEventListener('click', ev => {
        if (ev.target.id === 'po-create') create();
        else if (ev.target.id === 'po-vendor-find') findVendors();
    });
    root.addEventListener('keydown', ev => {
        if (ev.target.id === 'po-vendor-search' && ev.key === 'Enter') { ev.preventDefault(); findVendors(); }
    });
    root.addEventListener('change', ev => {
        if (ev.target.id === 'po-vendor-list' && ev.target.value) { cardCode = ev.target.value; load(); }
    });

    load();
})();
