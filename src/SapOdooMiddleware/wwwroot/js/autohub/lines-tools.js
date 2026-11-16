// All lines tab tools (/autohub/documents/{id}): search, filters (status, what the line needs next) and,
// while the invoice is being reviewed, multi-select with bulk actions (POST .../issues/bulk) that update the
// rows in place. Filters and search are kept per invoice for the browser session.
(function () {
    const tools = document.getElementById('ln-tools');
    const table = document.getElementById('lines-table');
    if (!tools || !table) return;
    const EDITABLE = tools.dataset.editable === 'true';
    const docId = (document.getElementById('inv-tabs') || {}).dataset?.docId || location.pathname.split('/')[3];
    const base = '/api/autohub/documents/' + docId;
    const API_KEY = document.querySelector('meta[name="api-key"]')?.content || '';
    const search = document.getElementById('ln-search');
    const statusSel = document.getElementById('ln-status');
    const nextSel = document.getElementById('ln-next');
    const shown = document.getElementById('ln-shown');
    const bulk = document.getElementById('ln-bulk');
    const allBox = document.getElementById('ln-all');
    const stateKey = 'autohub-lines-filter:' + docId;

    let nextById = {};     // line id -> next action (from GET .../issues)
    const rows = () => Array.from(table.querySelectorAll('tr.line-row'));
    const detailFor = id => table.querySelector('tr.detail-row[data-line-id="' + id + '"]');
    const esc = s => (s == null ? '' : String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])));

    const NEXT_LABEL = {
        create_failed: 'Creation failed', fix_data: 'Missing price / quantity', confirm_match: 'Confirm SAP match',
        check_part: 'Check the matched part', manual: 'Needs manual handling', awaiting_enrichment: 'Waiting for lookup',
        assign_marque: 'Pick the marque', accept_marque: 'Accept the DGX marque', confirm_marque: 'Confirm the marque', sku_range_full: 'SKU range full',
        confirm_borrow: 'Confirm borrowed part', queue: 'Ready to queue', price: 'Price not approved',
        ready: 'Ready to create', created: 'Created', matched: 'Matched', skipped: 'Skipped',
    };

    async function api(method, path, payload) {
        const opts = { method, headers: {} };
        if (API_KEY) opts.headers['X-Api-Key'] = API_KEY;
        if (payload !== undefined) { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(payload); }
        const r = await fetch(base + path, opts);
        const text = await r.text();
        const json = text ? JSON.parse(text) : null;
        if (!r.ok) throw new Error((json && json.error) || ('HTTP ' + r.status));
        return json;
    }

    // ---- filters ----
    function saveState() {
        try { sessionStorage.setItem(stateKey, JSON.stringify({ q: search.value, s: statusSel.value, n: nextSel.value })); } catch (_) { }
    }
    function loadState() {
        try {
            const st = JSON.parse(sessionStorage.getItem(stateKey) || 'null');
            if (st) { search.value = st.q || ''; statusSel.value = st.s || ''; nextSel.dataset.want = st.n || ''; }
        } catch (_) { }
    }

    function applyFilters() {
        const q = search.value.trim().toLowerCase();
        const st = statusSel.value, nx = nextSel.value;
        let n = 0, total = 0;
        rows().forEach(r => {
            total++;
            const text = (r.dataset.searchText ||= r.textContent.toLowerCase());
            const ok = (!q || text.includes(q))
                && (!st || r.dataset.reviewStatus === st)
                && (!nx || nextById[r.dataset.lineId] === nx);
            r.style.display = ok ? '' : 'none';
            const d = detailFor(r.dataset.lineId);
            if (d && !ok) d.style.display = 'none';
            if (ok) n++;
        });
        shown.textContent = (n === total ? 'All ' + total + ' lines' : 'Showing ' + n + ' of ' + total);
        saveState();
        renderBulk();
    }

    async function loadNext() {
        try {
            const d = await api('GET', '/issues');
            nextById = {};
            const counts = d.summary.by_next_action || {};
            (d.groups || []).forEach(g => g.lines.forEach(l => { nextById[l.line_id] = l.next_action; }));
            // Lines not in a group are ready or done; mark them from their status.
            rows().forEach(r => {
                const id = r.dataset.lineId;
                if (nextById[id]) return;
                const s = r.dataset.reviewStatus;
                nextById[id] = s === 'created' ? 'created' : s === 'matched' ? 'matched' : s === 'skip' ? 'skipped' : 'ready';
            });
            const want = nextSel.value || nextSel.dataset.want || '';
            const order = ['create_failed', 'fix_data', 'confirm_match', 'check_part', 'manual', 'awaiting_enrichment', 'assign_marque',
                'accept_marque', 'confirm_marque', 'sku_range_full', 'confirm_borrow', 'queue', 'price', 'ready', 'created', 'matched', 'skipped'];
            nextSel.innerHTML = '<option value="">Anything needed</option>' + order.filter(c => counts[c])
                .map(c => '<option value="' + c + '">' + esc(NEXT_LABEL[c] || c) + ' (' + counts[c] + ')</option>').join('');
            if (want && counts[want]) nextSel.value = want;
            delete nextSel.dataset.want;
            if (window.updateInvoiceTabs) window.updateInvoiceTabs(d.summary);
        } catch (_) { /* filters still work without it */ }
        applyFilters();
    }

    // ---- selection + bulk ----
    const selectedRows = () => rows().filter(r => r.style.display !== 'none' && r.querySelector('.ln-sel')?.checked);

    function renderBulk() {
        if (!EDITABLE) { bulk.innerHTML = ''; return; }
        const sel = selectedRows();
        if (allBox) {
            const visible = rows().filter(r => r.style.display !== 'none');
            allBox.checked = visible.length > 0 && sel.length === visible.length;
        }
        if (!sel.length) { bulk.innerHTML = ''; return; }
        if (bulk.dataset.n === String(sel.length) && bulk.childElementCount) return;   // keep a half-typed marque
        bulk.dataset.n = String(sel.length);
        bulk.innerHTML = '<strong>' + sel.length + ' selected</strong>' +
            '<button type="button" class="btn btn-sm btn-primary" data-act="queue" title="Mark looked-up lines to create">Mark to create</button>' +
            '<button type="button" class="btn btn-sm" data-act="confirm_and_queue" title="Confirm the matched / borrowed part and mark to create">Confirm part</button>' +
            '<button type="button" class="btn btn-sm" data-act="accept_marque" title="Apply the DGX\'s resolved marque">Accept DGX marque</button>' +
            '<button type="button" class="btn btn-sm" data-act="keep_marque" title="Keep the marque the line will be created under">Keep marque</button>' +
            '<input class="mq" id="ln-mq" placeholder="Marque" maxlength="10"><button type="button" class="btn btn-sm" data-act="assign_marque">Assign marque</button>' +
            '<button type="button" class="btn btn-sm" data-act="skip">Skip</button>' +
            '<button type="button" class="btn btn-sm" data-act="clear">Clear</button>';
    }

    async function runBulk(action) {
        const sel = selectedRows();
        if (!sel.length) return;
        const extra = {};
        if (action === 'assign_marque') {
            const code = (document.getElementById('ln-mq')?.value || '').trim().toUpperCase();
            if (!code || code === 'GEN') { alert('Enter a real marque, e.g. MB, BM, VAG.'); return; }
            extra.manufacturer_code = code;
        }
        if (action === 'skip' && !confirm('Skip ' + sel.length + ' line(s)? They will not be created or matched.')) return;
        bulk.querySelectorAll('button').forEach(b => b.disabled = true);
        let r;
        try { r = await api('POST', '/issues/bulk', Object.assign({ action, line_ids: sel.map(x => x.dataset.lineId) }, extra)); }
        catch (e) { alert('Could not do that: ' + e.message); bulk.dataset.n = ''; renderBulk(); return; }

        const byId = Object.fromEntries(sel.map(x => [x.dataset.lineId, x]));
        const fails = [];
        r.results.forEach(x => {
            const row = byId[x.line_id];
            if (!row) return;
            if (x.ok) {
                if (x.review_status && window.invoiceSetRowPill) window.invoiceSetRowPill(row, x.review_status);
                if (action === 'confirm_and_queue')
                    row.querySelectorAll('.status-cell .pill-orange').forEach(p => { if (p.textContent.includes('check part')) p.remove(); });
                const box = detailFor(x.line_id)?.querySelector('.ld-enrichment');
                if (box) box.dataset.loaded = 'false';          // reload the panel next time it opens
                row.querySelector('.ln-sel').checked = false;
                delete row.dataset.searchText;
            } else {
                fails.push('Line ' + (row.children[EDITABLE ? 1 : 0]?.textContent || '?').trim() + ': ' + x.error);
            }
        });
        bulk.dataset.n = '';
        if (fails.length) alert(r.succeeded + ' done, ' + fails.length + ' not done:\n' + fails.join('\n'));
        if (window.invoiceRefreshSummary) window.invoiceRefreshSummary();
        await loadNext();
    }

    // ---- events ----
    search.addEventListener('input', applyFilters);
    statusSel.addEventListener('change', applyFilters);
    nextSel.addEventListener('change', applyFilters);
    table.addEventListener('change', ev => { if (ev.target.classList.contains('ln-sel')) renderBulk(); });
    allBox?.addEventListener('change', () => {
        rows().filter(r => r.style.display !== 'none').forEach(r => { const c = r.querySelector('.ln-sel'); if (c) c.checked = allBox.checked; });
        renderBulk();
    });
    bulk.addEventListener('click', ev => {
        const b = ev.target.closest('[data-act]');
        if (!b) return;
        if (b.dataset.act === 'clear') { rows().forEach(r => { const c = r.querySelector('.ln-sel'); if (c) c.checked = false; }); renderBulk(); return; }
        runBulk(b.dataset.act);
    });

    loadState();
    applyFilters();
    loadNext();
})();
