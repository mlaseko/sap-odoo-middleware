// Autohub invoice Issues tab (/autohub/documents/{id}/issues).
// Lines that need attention, grouped by what they need next (GET .../issues), bulk and per-line actions
// (POST .../issues/bulk), and a resolution card per line: invoice line · matched part · what will be created.
(function () {
    const root = document.getElementById('is-root');
    if (!root) return;
    const docId = root.dataset.docId;
    const EDITABLE = root.dataset.editable === 'true';
    const base = '/api/autohub/documents/' + docId;
    const invoiceUrl = '/autohub/documents/' + docId;
    const API_KEY = document.querySelector('meta[name="api-key"]')?.content || '';
    const groupsEl = document.getElementById('is-groups');
    const panel = document.getElementById('is-panel');
    const msg = document.getElementById('is-message');

    let data = null;               // last GET /issues
    let groupCode = null;          // selected group
    const selected = new Set();    // selected line ids (survive re-renders)
    const openCards = new Set();   // line ids with the resolution card open
    const enrCache = {};           // lineId -> enrichment payload (or { error })
    let groupNames = {};           // SAP item group code -> name
    let busy = false;
    let marqueOpen = false;        // assign-marque form open (survives re-renders)
    const marqueDraft = { code: '', group: '' };

    // ---- helpers ----
    async function api(method, path, payload) {
        const opts = { method, headers: {} };
        if (API_KEY) opts.headers['X-Api-Key'] = API_KEY;
        if (payload !== undefined) { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(payload); }
        // '/api/...' is a full path; anything else is relative to this invoice's API (base).
        const r = await fetch(path.startsWith('/api/') ? path : base + path, opts);
        if (r.status === 204) return null;
        const text = await r.text();
        const json = text ? JSON.parse(text) : null;
        if (!r.ok) throw new Error((json && json.error) || ('HTTP ' + r.status));
        return json;
    }
    const esc = s => (s == null ? '' : String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])));
    const tzs = v => v == null ? '–' : Math.round(Number(v)).toLocaleString();
    const money = v => v == null ? '–' : Number(v).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const normOem = s => String(s || '').replace(/[\s./\-]/g, '').toUpperCase();
    const groupText = c => c == null || c === '' ? '–' : esc(c) + (groupNames[String(c)] ? ' · ' + esc(groupNames[String(c)]) : '');
    function showMessage(html, kind) {
        msg.innerHTML = html ? '<div class="' + (kind === 'error' ? 'note-error' : 'notice') + '">' + html + '</div>' : '';
    }

    const SOURCE_LABEL = {
        tecdoc_direct: 'TecDoc: exact article match for this supplier',
        borrowed_oem_bridge: "Another supplier's part with the same OEM number",
        germax_local: 'Germax catalogue',
        rapidapi_tecdoc_live: 'TecDoc live search by OEM / article number',
        rapidapi_tecdoc: 'TecDoc search, chosen by an operator',
        unmatched: 'No catalogue match',
    };

    // Button labels per action; confirm_and_queue reads differently per group.
    function actionLabel(action, group) {
        if (action === 'confirm_and_queue')
            return ({ check_part: 'Part is correct, confirm', confirm_match: 'Create a new item instead',
                      confirm_borrow: 'Confirm borrowed part' })[group] || 'Confirm and mark to create';
        return ({ skip: 'Skip', queue: 'Mark to create', use_suggested_match: 'Use suggested SAP item',
                  accept_marque: 'Accept DGX marque', keep_marque: 'Keep this marque', assign_marque: 'Assign marque…', retry_create: 'Retry creating',
                  open_prices: 'Open price review' })[action] || action;
    }
    const PRIMARY = new Set(['confirm_and_queue', 'queue', 'accept_marque', 'keep_marque', 'use_suggested_match', 'retry_create']);
    // Groups where choosing another part (the donor window on the invoice) is a sensible fix.
    const PART_GROUPS = new Set(['check_part', 'manual', 'confirm_borrow', 'confirm_match']);

    // ---- load ----
    async function load(keepScroll) {
        const y = window.scrollY;
        try { data = await api('GET', '/issues'); }
        catch (e) { panel.innerHTML = '<div class="note-error">Could not load the issues: ' + esc(e.message) + '</div>'; return; }
        if (window.updateInvoiceTabs) window.updateInvoiceTabs(data.summary);
        const codes = data.groups.map(g => g.code);
        const fromHash = (/#group=([a-z_]+)/.exec(location.hash) || [])[1];
        if (fromHash && codes.includes(fromHash)) groupCode = fromHash;
        if (!groupCode || !codes.includes(groupCode)) groupCode = codes[0] || null;
        // Forget selections of lines that left the open groups.
        const open = new Set(data.groups.flatMap(g => g.lines.map(l => l.line_id)));
        [...selected].forEach(id => { if (!open.has(id)) selected.delete(id); });
        render();
        if (keepScroll) window.scrollTo(0, y);
        armRefresh();
    }

    // While the DGX is still looking parts up, refresh now and then (not while the operator is mid-selection).
    let refreshTimer = null;
    function armRefresh() {
        clearTimeout(refreshTimer);
        if (!data || !(data.summary.by_next_action || {}).awaiting_enrichment) return;
        refreshTimer = setTimeout(() => {
            if (!document.hidden && !busy && selected.size === 0) load(true); else armRefresh();
        }, 20000);
    }

    // ---- render ----
    function render() {
        renderGroups();
        renderPanel();
    }

    function renderGroups() {
        const s = data.summary;
        let html = '<div class="sub" style="margin:0 0 6px">' + s.total + ' lines · ' + s.done + ' done · ' + s.ready +
            ' ready · <strong>' + s.with_issues + '</strong> need attention</div>';
        if (!data.groups.length) html += '<div class="sub">Nothing needs attention.</div>';
        html += data.groups.map(g =>
            '<button type="button" class="is-group' + (g.code === groupCode ? ' active' : '') + (g.bulk_actions.length ? '' : ' info') +
            '" data-group="' + g.code + '"><span>' + esc(g.title) + '</span><span class="n">' + g.lines.length + '</span></button>').join('');
        groupsEl.innerHTML = html;
    }

    function currentGroup() { return data.groups.find(g => g.code === groupCode) || null; }

    function renderPanel() {
        const g = currentGroup();
        if (!g) {
            panel.innerHTML = '<div class="is-empty"><h3>No issues left on this invoice</h3>' +
                '<p>' + data.summary.ready + ' line(s) are ready to create. Next: check the prices, then create the items.</p>' +
                '<p><a class="btn btn-primary" href="' + invoiceUrl + '/prices">Price review</a> ' +
                '<a class="btn" href="' + invoiceUrl + '">All lines</a></p></div>';
            return;
        }
        const ids = g.lines.map(l => l.line_id);
        const nSel = ids.filter(id => selected.has(id)).length;
        let html = '<div class="is-head"><h3>' + esc(g.title) + ' (' + g.lines.length + ')</h3><p class="help">' + esc(g.help) + '</p></div>';

        if (EDITABLE && g.bulk_actions.length) {
            html += '<div class="is-bulk"><label><input type="checkbox" id="is-all"' + (nSel && nSel === ids.length ? ' checked' : '') +
                '> <span class="sel">' + (nSel ? nSel + ' selected' : 'Select all') + '</span></label>';
            g.bulk_actions.forEach(a => {
                if (a === 'open_prices') { html += '<a class="btn btn-sm btn-primary" href="' + invoiceUrl + '/prices">Open price review</a>'; return; }
                html += '<button type="button" class="btn btn-sm' + (PRIMARY.has(a) ? ' btn-primary' : '') + '" data-bulk="' + a + '"' +
                    (nSel ? '' : ' disabled title="Select lines first"') + '>' + esc(actionLabel(a, g.code)) + (nSel ? ' (' + nSel + ')' : '') + '</button>';
            });
            if (g.bulk_actions.includes('assign_marque')) html += marqueFormHtml(g);
            html += '</div>';
        }

        html += '<table id="is-table"><thead><tr>' + (EDITABLE && g.bulk_actions.length ? '<th></th>' : '') +
            '<th class="num">#</th><th>Part</th><th class="num">Qty</th><th class="num">Invoice price</th><th>What is needed</th><th></th></tr></thead><tbody>';
        html += g.lines.map(l => rowHtml(l, g)).join('');
        html += '</tbody></table>';
        panel.innerHTML = html;
        g.lines.forEach(l => { if (openCards.has(l.line_id)) loadCard(l); });
    }

    function marqueFormHtml(g) {
        const sugg = [...new Set(g.lines.map(l => l.marque_suggestion).filter(Boolean))];
        const groups = Object.keys(groupNames).sort((a, b) => Number(a) - Number(b));
        return '<span class="marque-form' + (marqueOpen ? ' open' : '') + '" id="is-mq">' +
            '<input class="mq" id="is-mq-code" list="is-mq-list" placeholder="Marque" maxlength="10" value="' + esc(marqueDraft.code) + '">' +
            '<datalist id="is-mq-list">' + sugg.map(s => '<option value="' + esc(s) + '">').join('') + '</datalist>' +
            '<select id="is-mq-group"><option value="">Keep the DGX item group</option>' +
            groups.map(c => '<option value="' + esc(c) + '"' + (c === marqueDraft.group ? ' selected' : '') + '>' + esc(c) + ' · ' + esc(groupNames[c]) + '</option>').join('') + '</select>' +
            '<button type="button" class="btn btn-sm btn-primary" id="is-mq-go">Assign</button>' +
            '<button type="button" class="btn btn-sm" id="is-mq-cancel">Cancel</button></span>';
    }

    function rowHtml(l, g) {
        const bulkCol = EDITABLE && g.bulk_actions.length;
        const issues = (l.issues || []).map((i, k) => '<li class="' + (k === 0 ? 'primary' : '') + '">' + esc(i.text) + '</li>').join('');
        let actions = '';
        if (EDITABLE) {
            g.bulk_actions.filter(a => a !== 'open_prices').forEach(a => {
                actions += '<button type="button" class="btn btn-sm' + (PRIMARY.has(a) ? ' btn-primary' : '') + '" data-one="' + a + '">' +
                    esc(actionLabel(a, g.code)) + '</button>';
            });
            if (PART_GROUPS.has(g.code))
                actions += '<button type="button" class="btn btn-sm" data-choose-part="1">Choose another part…</button>';
            if (g.code === 'fix_data')
                actions += '<a class="btn btn-sm btn-primary" href="' + invoiceUrl + '#line-' + l.line_id + '">Edit the line</a>';
        }
        actions += '<a class="btn btn-sm" href="' + invoiceUrl + '#line-' + l.line_id + '">Open on invoice</a>';
        return '<tr class="is-row" data-line-id="' + l.line_id + '">' +
            (bulkCol ? '<td><input type="checkbox" class="is-sel"' + (selected.has(l.line_id) ? ' checked' : '') + '></td>' : '') +
            '<td class="num">' + l.line_number + '</td>' +
            '<td><div>' + esc(l.description || '') + '</div><div class="sub">' + esc(l.article_number || '') +
                (l.brand ? ' · ' + esc(l.brand) : '') + '</div></td>' +
            '<td class="num">' + (l.quantity != null ? Number(l.quantity) : '–') + '</td>' +
            '<td class="num">' + money(l.unit_price_foreign) + '</td>' +
            '<td><ul class="issues">' + issues + '</ul></td>' +
            '<td><div class="is-actions">' + actions +
                '<button type="button" class="btn btn-sm" data-card="1">' + (openCards.has(l.line_id) ? 'Hide details' : 'Details') + '</button></div></td>' +
            '</tr>' +
            (openCards.has(l.line_id) ? '<tr class="is-card" data-card-for="' + l.line_id + '"><td colspan="' + (bulkCol ? 7 : 6) +
                '"><div class="card-body">Loading…</div></td></tr>' : '');
    }

    // TecDoc fitment is one long string ("MERCEDES-BENZ CLS (C218) CLS 350 CDI (218.323) MERCEDES-BENZ G-CLASS …").
    // Show one vehicle per line: split before each repeat of a make word that starts the string or follows ')'.
    function splitFitment(text) {
        const t = String(text || '').trim();
        if (!t) return [];
        const makes = new Set([t.split(/\s+/)[0]]);
        // Another make is a word that starts a vehicle (comes right after ')') at least twice; a model
        // word such as "CLS" follows ')' only once.
        const after = {};
        (t.match(/\)\s+[A-Z][A-Z-]{2,}(?=\s)/g) || []).forEach(m => {
            const w = m.replace(/^\)\s+/, '');
            after[w] = (after[w] || 0) + 1;
        });
        Object.keys(after).forEach(w => { if (after[w] >= 2) makes.add(w); });
        const re = new RegExp('\\s+(?=(?:' + [...makes].map(m => m.replace(/[-]/g, '\\-')).join('|') + ')\\s)', 'g');
        return t.split(re).map(x => x.trim()).filter(Boolean);
    }

    // ---- resolution card: invoice line · matched part · what will be created ----
    async function loadCard(l) {
        if (!(l.line_id in enrCache)) {
            try { enrCache[l.line_id] = await api('GET', '/lines/' + l.line_id + '/enrichment') || { none: true }; }
            catch (e) { enrCache[l.line_id] = { error: e.message }; }
        }
        const cell = panel.querySelector('tr[data-card-for="' + l.line_id + '"] .card-body');
        if (cell) cell.innerHTML = cardHtml(l, enrCache[l.line_id]);
    }

    function cardHtml(l, enr) {
        const lineOems = new Set((l.oem_numbers || []).flatMap(o => String(o).split(/[\s,;/]+/)).map(normOem).filter(Boolean));
        const invoice = '<div><h4>Invoice line</h4><table class="kv">' +
            '<tr><th>Description</th><td>' + esc(l.description) + '</td></tr>' +
            '<tr><th>Article</th><td>' + esc(l.article_number) + '</td></tr>' +
            '<tr><th>Brand</th><td>' + esc(l.brand) + '</td></tr>' +
            '<tr><th>OEMs</th><td>' + esc((l.oem_numbers || []).join(', ') || '–') + '</td></tr>' +
            '<tr><th>Qty · price</th><td>' + (l.quantity != null ? Number(l.quantity) : '–') + ' × ' + money(l.unit_price_foreign) + '</td></tr>' +
            '</table></div>';

        let part;
        if (enr.error) part = '<div><h4>Matched part</h4><span class="note-error">' + esc(enr.error) + '</span></div>';
        else if (enr.none || !enr.item_data) part = '<div><h4>Matched part</h4><p class="sub">' +
            (enr.none ? 'Not looked up yet.' : 'No catalogue match was found.') + '</p></div>';
        else {
            const d = enr.item_data, bf = enr.borrowed_from, sel = enr.audit && enr.audit.selection;
            const src = enr.enrichment_source || enr.source;
            const oems = (d.filtered_oems || []).map(o => lineOems.has(normOem(o))
                ? '<span class="oem-hit" title="Also on the invoice line">' + esc(o) + '</span>' : esc(o)).join(', ');
            const fit = splitFitment(d.fit_for_auto);
            const pic = enr.picture_url || d.image_url;
            part = '<div><h4>Matched part</h4>' + (pic
                    ? '<img src="' + esc(pic) + '" alt="" data-zoom="' + esc(pic) + '" title="Click to enlarge" style="cursor:zoom-in" onerror="this.replaceWith(Object.assign(document.createElement(\'div\'),{className:\'no-pic\',textContent:\'Picture could not be loaded\'}))">'
                    : '<div class="no-pic">No picture for this part</div>') +
                '<table class="kv">' +
                '<tr><th>Name</th><td>' + esc(d.primary_description || (sel && sel.selected_name) || '–') + '</td></tr>' +
                (sel && sel.selected_supplier ? '<tr><th>Supplier</th><td>' + esc(sel.selected_supplier) + '</td></tr>' : '') +
                (bf ? '<tr><th>Borrowed from</th><td>' + esc(bf.article_number) + (bf.supplier_name ? ' (' + esc(bf.supplier_name) + ')' : '') + '</td></tr>' : '') +
                '<tr><th>Found by</th><td>' + esc(SOURCE_LABEL[src] || src || '–') + '</td></tr>' +
                (sel && sel.needs_review ? '<tr><th>Part check</th><td>' +
                    (String(sel.selected_component_verdict).toUpperCase() === 'DIFFERENT' ? 'Looks like a different kind of part' : 'Not confirmed as the same kind of part') + '</td></tr>' : '') +
                '<tr><th>OEMs</th><td>' + (oems || '–') + '</td></tr>' +
                '<tr><th>Fits</th><td><div class="fit-list">' + (fit.filter(Boolean).map(esc).join('<br>') || '–') + '</div></td></tr>' +
                '</table></div>';
        }

        const d = (enr && enr.item_data) || {};
        const mr = enr && enr.manufacturer_resolution;
        const prefix = d.suggested_sku_prefix && String(d.suggested_sku_prefix).toUpperCase() !== 'GEN' ? d.suggested_sku_prefix : null;
        const marque = prefix || (mr && mr.resolved && mr.prefix ? mr.prefix + ' (DGX, not confirmed)' : (l.marque_suggestion ? '? (DGX guess: ' + l.marque_suggestion + ')' : '?'));
        const priceText = l.pl03 != null
            ? tzs(l.pl03) + ' <span class="sub">(' + (l.pl03_approved ? 'approved' : l.price_state === 'held' ? 'held' : 'calculated, not approved') + ')</span>'
            : '<span class="sub">priced once queued to create</span>';
        const created = '<div><h4>Will be created in SAP</h4><table class="kv">' +
            '<tr><th>Item code</th><td>' + (prefix ? esc(prefix) + '…' : '–') + ' <span class="sub">(next number for the prefix)</span></td></tr>' +
            '<tr><th>Marque</th><td>' + esc(marque) + '</td></tr>' +
            '<tr><th>SAP group</th><td>' + groupText(d.suggested_itms_grp_cod) + '</td></tr>' +
            '<tr><th>Part name</th><td>' + esc(d.primary_description || l.description || '–') + '</td></tr>' +
            '<tr><th>PL03</th><td>' + priceText + '</td></tr>' +
            '</table></div>';
        return '<div class="card3">' + invoice + part + created + '</div>';
    }

    // ---- actions ----
    async function runAction(action, ids, extra, clicked) {
        if (!ids.length || busy) return;
        const g = currentGroup();
        if (action === 'skip' && !confirm('Skip ' + ids.length + ' line(s)? They will not be created or matched.')) return;
        busy = true;
        const label = esc(actionLabel(action, g && g.code).replace(/…$/, ''));
        showMessage('<span class="busy-spin small"></span> ' + label + ': working on ' + ids.length + ' line(s)…');
        // Every action button waits until this one is done; the clicked one shows it is working.
        const Busy = window.AutohubBusy;
        const restoreBtn = Busy ? Busy.button(clicked, 'Working…') : () => { };
        const frozen = Array.from(panel.querySelectorAll('button, input, select')).filter(el => !el.disabled && el !== clicked);
        frozen.forEach(el => { el.disabled = true; });
        const done = Busy ? Busy.start(actionLabel(action, g && g.code).replace(/…$/, '') + ': working on ' + ids.length + ' line(s)…',
            ids.length > 5 ? 'Large batches can take a minute. Please wait.' : null) : () => { };
        try {
            const r = await api('POST', '/issues/bulk', Object.assign({ action, line_ids: ids }, extra || {}));
            const byId = Object.fromEntries((g ? g.lines : []).map(l => [l.line_id, l]));
            const fails = r.results.filter(x => !x.ok);
            let html = esc(actionLabel(action, g && g.code).replace(/…$/, '')) + ': ' + r.succeeded + ' done' + (fails.length ? ', ' + fails.length + ' not done' : '') + '.';
            if (r.note) html += ' ' + esc(r.note);
            if (fails.length) html += '<ul>' + fails.map(f => '<li>Line ' + esc(byId[f.line_id] ? byId[f.line_id].line_number : '?') +
                ': ' + esc(f.error) + '</li>').join('') + '</ul>';
            showMessage(html, fails.length && !r.succeeded ? 'error' : null);
            ids.forEach(id => { selected.delete(id); delete enrCache[id]; });
            await load(true);
            if (action === 'retry_create' && r.job_id) setTimeout(() => load(true), 6000);
        } catch (e) {
            showMessage('Could not do that: ' + esc(e.message), 'error');
        } finally {
            busy = false;
            done();
            restoreBtn();
            frozen.forEach(el => { if (el.isConnected) el.disabled = false; });
        }
    }

    groupsEl.addEventListener('click', ev => {
        const b = ev.target.closest('[data-group]');
        if (!b) return;
        groupCode = b.dataset.group;
        history.replaceState(null, '', '#group=' + groupCode);
        showMessage('');
        render();
        window.scrollTo(0, 0);
    });

    panel.addEventListener('input', ev => {
        if (ev.target.id === 'is-mq-code') marqueDraft.code = ev.target.value;
    });

    panel.addEventListener('change', ev => {
        const g = currentGroup();
        if (!g) return;
        if (ev.target.id === 'is-mq-group') { marqueDraft.group = ev.target.value; return; }
        if (ev.target.id === 'is-all') {
            g.lines.forEach(l => ev.target.checked ? selected.add(l.line_id) : selected.delete(l.line_id));
            renderPanel();
        } else if (ev.target.classList.contains('is-sel')) {
            const id = ev.target.closest('tr').dataset.lineId;
            ev.target.checked ? selected.add(id) : selected.delete(id);
            renderPanel();
        }
    });

    panel.addEventListener('click', ev => {
        const g = currentGroup();
        if (!g) return;
        const bulk = ev.target.closest('[data-bulk]');
        if (bulk) {
            const ids = g.lines.map(l => l.line_id).filter(id => selected.has(id));
            if (bulk.dataset.bulk === 'assign_marque') { openMarqueForm(); return; }
            runAction(bulk.dataset.bulk, ids, null, bulk);
            return;
        }
        const tr = ev.target.closest('tr.is-row');
        if (ev.target.closest('[data-choose-part]') && tr) { choosePart(tr.dataset.lineId); return; }
        const one = ev.target.closest('[data-one]');
        if (one && tr) {
            if (one.dataset.one === 'assign_marque') {
                selected.clear(); selected.add(tr.dataset.lineId);
                renderPanel(); openMarqueForm();
                return;
            }
            runAction(one.dataset.one, [tr.dataset.lineId], null, one);
            return;
        }
        if (ev.target.closest('[data-card]') && tr) {
            const id = tr.dataset.lineId;
            openCards.has(id) ? openCards.delete(id) : openCards.add(id);
            renderPanel();
            return;
        }
        if (ev.target.id === 'is-mq-cancel') { marqueOpen = false; document.getElementById('is-mq').classList.remove('open'); return; }
        if (ev.target.id === 'is-mq-go') {
            const code = (document.getElementById('is-mq-code').value || '').trim().toUpperCase();
            const grp = document.getElementById('is-mq-group').value;
            const ids = g.lines.map(l => l.line_id).filter(id => selected.has(id));
            if (!code) { showMessage('Enter a marque, e.g. MB, BM, VAG.', 'error'); return; }
            if (code === 'GEN') { showMessage('GEN is not a marque: pick the real manufacturer.', 'error'); return; }
            marqueOpen = false; marqueDraft.code = ''; marqueDraft.group = '';
            runAction('assign_marque', ids, { manufacturer_code: code, items_group_code: grp ? Number(grp) : null }, ev.target);
        }
    });

    function openMarqueForm() {
        const f = document.getElementById('is-mq');
        if (!f) return;
        marqueOpen = true;
        f.classList.add('open');
        const g = currentGroup();
        const firstSel = g && g.lines.find(l => selected.has(l.line_id));
        const input = document.getElementById('is-mq-code');
        if (firstSel && firstSel.marque_suggestion && !input.value) input.value = marqueDraft.code = firstSel.marque_suggestion;
        input.focus();
    }

    // ---- choose another part: the shared donor window (donor-picker.js), opened here ----
    const overlay = document.getElementById('modal-overlay');
    const mBody = document.getElementById('modal-body');
    const mActions = document.getElementById('modal-actions');
    const mBox = overlay ? overlay.querySelector('.modal') : null;
    function openModal(html, actions) {
        if (mBox) mBox.classList.remove('modal-wide');
        mBody.innerHTML = html;
        mActions.innerHTML = '';
        actions.forEach(a => {
            const b = document.createElement('button');
            b.className = 'btn ' + (a.primary ? 'btn-primary' : '');
            b.textContent = a.label;
            b.onclick = a.onClick;
            mActions.appendChild(b);
        });
        overlay.style.display = 'flex';
    }
    function closeModal() { overlay.style.display = 'none'; }
    if (overlay) {
        const busyModal = () => mBox && mBox.dataset.busy;   // a part change is running: wait for it
        overlay.addEventListener('mousedown', e => { if (e.target === overlay && !busyModal()) closeModal(); });
        document.addEventListener('keydown', e => { if (e.key === 'Escape' && overlay.style.display !== 'none' && !busyModal()) closeModal(); });
    }
    const donorPicker = overlay && window.AutohubDonorPicker
        ? window.AutohubDonorPicker({ api, esc, openModal, closeModal, mBody, mBox, base, apiKey: API_KEY })
        : null;

    async function choosePart(lineId) {
        if (!donorPicker) { location.href = invoiceUrl + '#line-' + lineId + '/donor'; return; }
        const l = (currentGroup()?.lines || []).find(x => x.line_id === lineId);
        await donorPicker.open(lineId, async res => {
            delete enrCache[lineId];
            showMessage('Line ' + (l ? l.line_number : '') + ': part changed to ' + esc(res && res.swapped_to || 'the chosen part') +
                (res && res.supplier ? ' (' + esc(res.supplier) + ')' : '') + '.');
            await load(true);
        });
    }

    // ---- start ----
    fetch('/api/sap/item-groups', { headers: API_KEY ? { 'X-Api-Key': API_KEY } : {} })
        .then(r => r.ok ? r.json() : [])
        .then(list => { (list || []).forEach(g => { groupNames[String(g.code)] = g.name; }); if (data) render(); })
        .catch(() => { });
    load(false);
})();
