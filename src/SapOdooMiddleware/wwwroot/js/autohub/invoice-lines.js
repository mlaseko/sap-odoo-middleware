// Autohub invoice page (/autohub/documents/{id}): line review, enrichment panel, marque, donor swap,
// bulk actions. Moved out of Detail.cshtml; page values come from data attributes on #review-banner.
// ---- Autohub Phase B review interactions (only active for editable/extracted documents) ----
// Reload the invoice page but come back to the same scroll position (used after toolbar actions).
function reloadKeepingPlace() {
    try { sessionStorage.setItem('autohub-invoice-scroll:' + location.pathname, String(window.scrollY)); } catch (_) { }
    location.reload();
}
(function restorePlace() {
    try {
        const k = 'autohub-invoice-scroll:' + location.pathname, y = sessionStorage.getItem(k);
        if (y !== null) { sessionStorage.removeItem(k); window.addEventListener('load', () => window.scrollTo(0, Number(y))); }
    } catch (_) { }
})();

(function () {
    const banner = document.getElementById('review-banner');
    if (!banner || banner.dataset.editable !== 'true') return;

    const docId = banner.dataset.docId;
    const base = '/api/autohub/documents/' + docId;

    const PILL = {
        matched: ['pill-green', 'matched'], create_new: ['pill-blue', 'create new'],
        skip: ['pill-grey', 'skip'], pending: ['pill-yellow', 'pending'],
        created: ['pill-green', 'created'], create_failed: ['pill-red', 'create failed'],
        needs_manual: ['pill-orange', 'needs manual'], needs_confirmation: ['pill-purple', 'confirm match'],
        needs_manufacturer: ['pill-orange', 'assign marque'], prefix_exhausted: ['pill-red', 'SKU range full']
    };

    const API_KEY = document.querySelector('meta[name="api-key"]')?.content || '';

    async function api(method, path, body) {
        const opts = { method, headers: {} };
        if (API_KEY) opts.headers['X-Api-Key'] = API_KEY;
        if (body !== undefined) { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body); }
        const r = await fetch(base + path, opts);
        const text = await r.text();
        const json = text ? JSON.parse(text) : null;
        if (!r.ok) throw new Error((json && json.error) || ('HTTP ' + r.status));
        return json;
    }

    const esc = s => (s == null ? '' : String(s).replace(/[&<>"]/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;' }[c])));
    const SYM = banner.dataset.sym || '';
    const fmtMoney = v => SYM + Number(v).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

    function setRowPill(row, status) {
        const [cls, label] = PILL[status] || ['pill-grey', status];
        row.querySelector('.status-cell').innerHTML = '<span class="pill ' + cls + '">' + label + '</span>';
        row.dataset.reviewStatus = status;   // keep the dataset in sync so a panel re-render reads the new status
    }

    // ---- review summary banner ----
    async function refreshSummary() {
        let s;
        try { s = await api('GET', '/review-summary'); } catch (e) { return; }
        const b = s.by_status || {};
        // Marque-labelling progress: how many still need a confirm (DGX resolved) vs a pick (DGX couldn't).
        const toConfirm = s.marque_to_confirm || 0, toPick = s.marque_to_pick || 0;
        const marque = (toConfirm + toPick) > 0
            ? ' &nbsp;|&nbsp; ⚑ Marque: <span class="rc">' + toConfirm + ' to confirm</span> · <span class="rc">' + toPick + ' to pick</span>'
            : '';
        document.getElementById('review-counts').innerHTML =
            '📋 Review: <span class="rc">' + (b.matched || 0) + ' matched</span> · ' +
            '<span class="rc">' + (b.created || 0) + ' created</span> · ' +
            '<span class="rc">' + (b.create_new || 0) + ' to create</span> · ' +
            '<span class="rc">' + (b.skip || 0) + ' skipped</span> · ' +
            '<span class="rc">' + (b.needs_manual || 0) + ' needs manual</span> · ' +
            '<span class="rc">' + (b.needs_confirmation || 0) + ' confirm</span> · ' +
            '<span class="rc">' + (b.pending || 0) + ' pending</span> · ' +
            '<span class="rc">' + (b.create_failed || 0) + ' failed</span>' + marque;
        // Background-enrichment indicator: visible while DGX is still enriching this document's lines.
        const awaiting = s.awaiting_enrichment || 0;
        const ei = document.getElementById('enrich-indicator');
        const et = document.getElementById('enrich-text');
        if (ei) {
            if (awaiting > 0) {
                ei.style.display = '';
                if (et) et.textContent = 'Enrichment running… ' + awaiting + ' line' + (awaiting === 1 ? '' : 's') + ' to go';
            } else {
                ei.style.display = 'none';
            }
        }
        const skipN = (b.pending || 0) + (b.needs_manual || 0);
        const createN = (b.create_new || 0) + (b.create_failed || 0);   // failed lines are retried by Bulk Create
        const cnt = document.getElementById('cnt-createnew'); if (cnt) cnt.textContent = createN;
        const mp = document.getElementById('btn-markpending'); if (mp) mp.disabled = (b.pending || 0) === 0;
        const sp = document.getElementById('cnt-skip-pending'); if (sp) sp.textContent = skipN;
        const spb = document.getElementById('btn-skip-pending'); if (spb) spb.disabled = skipN === 0;
        const rs = document.getElementById('cnt-reopen-skipped'); if (rs) rs.textContent = b.skip || 0;
        const rsb = document.getElementById('btn-reopen-skipped'); if (rsb) rsb.disabled = (b.skip || 0) === 0;
        const bc = document.getElementById('btn-bulkcreate'); if (bc) bc.disabled = createN === 0;
        const cp = document.getElementById('btn-complete'); if (cp) cp.disabled = !s.can_complete;

        // Auto-poll only while there's live work to watch (enrichment running, or lines still in flight).
        // Once the document settles, stop the loop — operator actions refresh manually. A re-enrich/upload
        // that creates live work calls refreshSummary(), which re-arms the poll here.
        const live = awaiting > 0 || (b.pending || 0) > 0 || s.status === 'extracting' || s.status === 'uploaded';
        ensurePolling(live);
    }

    // ---- modal helpers ----
    const overlay = document.getElementById('modal-overlay');
    const mBox = overlay.querySelector('.modal');
    const mBody = document.getElementById('modal-body');
    const mActions = document.getElementById('modal-actions');
    // Set true by any user edit inside the modal (radio/select/text) so we can warn before an accidental close.
    let modalDirty = false;
    mBody.addEventListener('input',  () => { modalDirty = true; });
    mBody.addEventListener('change', () => { modalDirty = true; });

    function openModal(html, actions) {
        if (mBox) mBox.classList.remove('modal-wide');   // reset; wide is opt-in per modal
        mBody.innerHTML = html;
        mActions.innerHTML = '';
        actions.forEach(a => {
            const btn = document.createElement('button');
            btn.className = 'btn ' + (a.primary ? 'btn-primary' : '');
            btn.textContent = a.label;
            btn.onclick = a.onClick;
            mActions.appendChild(btn);
        });
        modalDirty = false;                 // fresh dialog
        overlay.style.display = 'flex';
    }
    function closeModal() { overlay.style.display = 'none'; modalDirty = false; }
    // Close via a dismiss gesture (Escape / click-outside): confirm first if the selection was changed.
    function requestCloseModal() {
        if (overlay.style.display === 'none') return;
        if (modalDirty && !confirm('You changed something in this dialog. Close without saving?')) return;
        closeModal();
    }
    // Escape closes any open modal; a click on the backdrop (not the dialog) does too — both guarded.
    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && overlay.style.display !== 'none') { e.preventDefault(); requestCloseModal(); }
    });
    overlay.addEventListener('mousedown', (e) => { if (e.target === overlay) requestCloseModal(); });

    // Unsaved inline line-edits (qty / price / description): warn before the browser leaves the page
    // (e.g. the Back button) so edits aren't silently lost. Cleared on a successful Save changes.
    let inlineDirty = false;
    document.getElementById('lines-table')?.addEventListener('input', (e) => {
        if (e.target.closest('.line-detail')) inlineDirty = true;
    });
    window.addEventListener('beforeunload', (e) => {
        if (modalDirty || inlineDirty) { e.preventDefault(); e.returnValue = ''; }
    });

    // ---- enrichment package -> readable summary ----
    // Marque picker for a held (needs_manufacturer) line: DGX couldn't resolve the manufacturer, so the
    // operator picks it. Candidates render in the order DGX sent (the order IS the ranking); zero-share
    // set-derived candidates are shown, not hidden; the reason drives the hold-label.
    function marquePickerHtml(mr, lineId) {
        const grp = 'mq-' + lineId;   // unique radio group per line — collapsed panels stay in the DOM,
                                       // so a shared name would merge every panel's radios into one group.
        const cands = mr.candidates || [];
        const reason = mr.reason === 'signal_conflict'
            ? 'Evidence disagrees — the candidates below show the dispute. Pick the correct marque.'
            : 'Not enough evidence to auto-resolve — pick from the brand\'s family (most likely first).';
        let html = '<div class="ld-marque">';
        html += '<h3>Assign manufacturer (marque)</h3>';
        html += '<p class="mq-reason">' + esc(reason) + '</p>';
        if (cands.length) {
            html += '<div class="mq-list">';
            cands.forEach((c, i) => {
                const share = (typeof c.share === 'number' && c.share > 0)
                    ? '<span class="mq-share">' + Math.round(c.share * 100) + '%</span>' : '';
                html += '<label class="mq-opt">' +
                    '<input type="radio" name="' + grp + '" value="' + esc(c.code) + '"' + (i === 0 ? ' checked' : '') + '>' +
                    '<span class="mq-head"><span class="mq-code">' + esc(c.code) + '</span>' +
                    '<span class="mq-label">' + esc(c.label) + '</span>' + share + '</span>' +
                    (c.evidence ? '<span class="mq-ev">' + esc(c.evidence) + '</span>' : '') +
                    '</label>';
            });
            html += '</div>';
        } else {
            // No candidates (rare) — fall back to a free-typed marque code.
            html += '<label class="mq-free">Marque code <input type="text" class="mq-freetext" placeholder="e.g. VAG"></label>';
        }
        html += '<div class="mq-actions"><button type="button" class="btn btn-sm mq-resolve">Resolve marque</button>' +
                '<span class="mq-status"></span></div>';
        html += '</div>';
        return html;
    }

    // When DGX already resolved the marque (voted/learned), the operator CONFIRMS the answer rather than
    // selecting blind — in shadow the line still holds under the GEN-guard, but the answer is attached.
    function marqueConfirmHtml(mr, lineId) {
        const grp = 'mq-' + lineId;   // unique radio group per line (see marquePickerHtml).
        const method = mr.method ? ' <span class="mq-share">(' + esc(mr.method) + ')</span>' : '';
        const ev = mr.evidence ? '<p class="mq-reason">' + esc(mr.evidence) + '</p>' : '';
        // DGX resolved it — the operator CONFIRMS the pick by default, but can OVERRIDE to another marque
        // (these brands are multi-marque, so a vote can be wrong). Confirm is preselected.
        return '<div class="ld-marque">' +
            '<h3>Manufacturer (marque)</h3>' +
            '<p class="mq-reason">✓ DGX resolved this to <strong>' + esc(mr.prefix) + '</strong>' + method + '. Confirm, or override if it\'s the wrong marque.</p>' +
            ev +
            '<div class="mq-list">' +
              '<label class="mq-opt"><input type="radio" name="' + grp + '" value="' + esc(mr.prefix) + '" checked>' +
                '<span class="mq-head"><span class="mq-code">' + esc(mr.prefix) + '</span><span class="mq-label">DGX pick — confirm</span></span></label>' +
              '<label class="mq-opt"><input type="radio" name="' + grp + '" value="__other__">' +
                '<span class="mq-head"><span class="mq-code">Override</span></span>' +
                '<span class="mq-ev"><input type="text" class="mq-freetext" placeholder="e.g. BM, MB"></span></label>' +
            '</div>' +
            '<div class="mq-actions"><button type="button" class="btn btn-sm mq-resolve">Resolve</button>' +
            '<span class="mq-status"></span></div>' +
            '</div>';
    }

    // Marque section for a line: confirm when DGX resolved it (has a prefix), pick when it couldn't.
    function marqueSectionHtml(mr, lineId) {
        return (mr.resolved === true && mr.prefix) ? marqueConfirmHtml(mr, lineId) : marquePickerHtml(mr, lineId);
    }

    function groupInputHtml(cur) {
        const codes = Object.keys(GROUP_NAMES);
        if (!codes.length)
            return '<input type="number" class="ov-group" value="' + esc(cur) + '" placeholder="e.g. 105">';
        const opts = codes.sort((a, b) => Number(a) - Number(b)).map(c =>
            '<option value="' + esc(c) + '"' + (c === String(cur) ? ' selected' : '') + '>' + esc(c) + ' · ' + esc(GROUP_NAMES[c]) + '</option>');
        if (cur && !GROUP_NAMES[String(cur)]) opts.unshift('<option value="' + esc(cur) + '" selected>' + esc(cur) + '</option>');
        return '<select class="ov-group">' + (cur ? '' : '<option value="">(keep)</option>') + opts.join('') + '</select>';
    }

    // Override for a line that already has a real marque/group (resolved or borrowed): a collapsed control
    // to correct a wrong auto-assigned marque (SKU prefix) or SAP item group. Saving posts to the same
    // resolve-manufacturer endpoint (marque via DGX + optional group override) and keeps the line queued.
    function marqueOverrideHtml(mr, d, lineId) {
        const curPrefix = ((mr && mr.prefix) || d.suggested_sku_prefix || '').toString();
        const curGroup  = (d.suggested_itms_grp_cod != null) ? String(d.suggested_itms_grp_cod) : '';
        return '<div class="ld-override">' +
            '<button type="button" class="btn btn-sm ov-toggle">✎ Change marque / group</button>' +
            '<div class="ov-form" style="display:none;">' +
              '<label class="ov-row"><span>Marque (SKU prefix)</span>' +
                '<input type="text" class="ov-marque" value="' + esc(curPrefix) + '" placeholder="e.g. BM, MB, VAG"></label>' +
              '<label class="ov-row"><span>SAP item group</span>' + groupInputHtml(curGroup) + '</label>' +
              '<div class="mq-actions"><button type="button" class="btn btn-sm ov-save">Save override</button>' +
                '<span class="ov-status"></span></div>' +
            '</div>' +
            '</div>';
    }

    // Plain-language names for the DGX enrichment sources.
    const SOURCE_LABEL = {
        tecdoc_direct: 'TecDoc: exact article match for this supplier',
        borrowed_oem_bridge: "Another supplier's part with the same OEM number (TecDoc)",
        germax_local: 'Germax catalogue (shared OEM number)',
        rapidapi_tecdoc_live: 'TecDoc live search by OEM / article number',
        rapidapi_tecdoc: 'TecDoc search, part chosen by an operator',
        unmatched: 'No catalogue match found',
    };
    const sourceText = s => s ? (SOURCE_LABEL[s] ? SOURCE_LABEL[s] + ' <span class="dc-dim">(' + esc(s) + ')</span>' : esc(s)) : 'unknown';

    // SAP item group names (GET /api/sap/item-groups, cached server-side), loaded once per page.
    let groupNamesPromise = null;
    function loadGroupNames() {
        if (!groupNamesPromise) groupNamesPromise = fetch('/api/sap/item-groups', { headers: API_KEY ? { 'X-Api-Key': API_KEY } : {} })
            .then(r => r.ok ? r.json() : [])
            .then(list => { const m = {}; (list || []).forEach(g => { m[String(g.code)] = g.name; }); return m; })
            .catch(() => ({}));
        return groupNamesPromise;
    }
    let GROUP_NAMES = {};
    const groupText = code => code == null || code === '' ? '' :
        esc(code) + (GROUP_NAMES[String(code)] ? ' · ' + esc(GROUP_NAMES[String(code)]) : '');

    // Why a line is flagged "check part": the DGX could not confirm the matched part is the same kind of
    // part as the invoice line (verdict MATCH / UNCERTAIN / DIFFERENT from the part-name comparison).
    function reviewReasonHtml(enr, lineId, reviewStatus) {
        const sel = enr.audit && enr.audit.selection;
        if (!sel || sel.needs_review !== true) return '';
        const row = document.querySelector('#lines-table tr.line-row[data-line-id="' + lineId + '"]');
        const invoiceDesc = row ? (row.querySelector('.col-desc')?.textContent || '') : '';
        const v = String(sel.selected_component_verdict || '').toUpperCase();
        const why = v === 'DIFFERENT'
            ? 'The matched part looks like a <strong>different kind of part</strong> from the invoice line.'
            : 'The matched part could not be confirmed as the <strong>same kind of part</strong> as the invoice line (the part names do not share a clear part type).';
        const canConfirm = ['pending', 'create_new', 'create_failed'].includes(reviewStatus);
        return '<div class="ld-review">' +
            '<div><strong>⚠ Check the part.</strong> ' + why + '</div>' +
            '<table class="kv"><tr><th>Invoice says</th><td>' + esc(invoiceDesc) + '</td></tr>' +
            '<tr><th>Matched part</th><td>' + esc(sel.selected_name || '?') +
                (sel.selected_supplier ? ' <span class="dc-dim">(' + esc(sel.selected_supplier) + ')</span>' : '') + '</td></tr></table>' +
            '<div class="mq-actions">' +
                '<button type="button" class="btn btn-sm ld-choose-donor">Choose another part…</button>' +
                (canConfirm ? '<button type="button" class="btn btn-sm ld-confirm-part">The part is correct, confirm</button>' : '') +
                '<span class="cp-status"></span>' +
            '</div></div>';
    }

    function enrichmentHtml(enr, reviewStatus, lineId) {
        const d = enr.item_data || {};
        const bf = enr.borrowed_from;
        const noData = !enr.item_data;
        // Lead with the marque section (pick or confirm) while the line is still actionable — not once it's
        // been created/matched/skipped. The resolve endpoint accepts any of those actionable states.
        const mr = enr.manufacturer_resolution;
        // Only in the inline review panel (which passes the line's status) — and not once terminal. Callers
        // that pass no status (e.g. the borrowed-enrichment confirm modal) deliberately suppress the section.
        const actionable = !!reviewStatus && reviewStatus !== 'created' && reviewStatus !== 'matched' && reviewStatus !== 'skip';
        // Only prompt for the marque while the stored prefix is still unresolved (blank / 'GEN'). Once a
        // real marque is applied (operator pick or bulk-confirm) the picker/confirm drops away and the KV
        // table below shows the resolved prefix — so re-rendering after resolve neither nags nor shows a
        // stale 'GEN'. Held/GEN lines (including create_new ones queued with GEN) still get the picker.
        const prefixUnresolved = !d.suggested_sku_prefix ||
            String(d.suggested_sku_prefix).trim().toUpperCase() === 'GEN';
        // Unresolved (blank/GEN) → the pick/confirm picker, expanded (the operator must act). Already
        // resolved with a real prefix → a collapsed "Change marque / group" override, so a wrong auto-
        // assigned marque (VAG→BM) or SAP group (105) can still be corrected without re-uploading.
        let head = '';
        if (mr && actionable && prefixUnresolved) head = marqueSectionHtml(mr, lineId);
        else if (actionable && !prefixUnresolved && !noData) head = marqueOverrideHtml(mr, d, lineId);
        // Enrichment already happened during extraction — show its outcome, don't imply it's pending.
        let html = head + (lineId && actionable ? reviewReasonHtml(enr, lineId, reviewStatus) : '') + '<h3>Item enrichment</h3>';
        html += '<p>Enrichment: <strong>' + (noData ? 'no catalog match found' : 'done') + '</strong>' +
                ' &middot; found by <strong>' + sourceText(enr.enrichment_source || enr.source) + '</strong>';
        // Only a genuine cross-supplier borrow needs sign-off before creating.
        if (bf) html += ' <span class="pill pill-yellow">borrowed — confirm before create</span>';
        html += '</p>';
        if (bf) {
            html += '<p class="ld-borrowed">Borrowed from article <strong>' + esc(bf.article_number) + '</strong>' +
                    (bf.supplier_name ? ' (' + esc(bf.supplier_name) + ')' : '') + '. Please verify the fit below.</p>';
            // Per-line sign-off for a cross-supplier borrow: marks the line confirmed (stays queued) so Bulk
            // Create's borrow gate passes. "Confirm all & Create" remains the bulk equivalent.
            if (actionable) html += '<div class="mq-actions"><button type="button" class="btn btn-sm ld-confirm-borrow">Confirm borrow (keep queued)</button><span class="cb-status"></span></div>';
        }
        html += '<table class="kv">';
        html += '<tr><th>Description</th><td>' + esc(d.primary_description) + '</td></tr>';
        html += '<tr><th>Fit&nbsp;for</th><td>' + esc(d.fit_for_auto) + '</td></tr>';
        html += '<tr><th>SAP group</th><td>' + groupText(d.suggested_itms_grp_cod) + '</td></tr>';
        html += '<tr><th>SKU prefix</th><td>' + esc(d.suggested_sku_prefix) + '</td></tr>';
        if (d.filtered_oems && d.filtered_oems.length) html += '<tr><th>OEMs</th><td>' + esc(d.filtered_oems.join(', ')) + '</td></tr>';
        html += '</table>';
        if (d.image_url) html += '<div style="margin-top:8px;"><img src="' + esc(d.image_url) + '" alt="" class="ld-img"/></div>';
        return html;
    }

    // ---- operator "choose borrowed donor" (swap) ----
    function verdictPill(v) {
        const cls = v === 'MATCH' ? 'pill-green' : (v === 'DIFFERENT' ? 'pill-red' : 'pill-yellow');
        return '<span class="pill ' + cls + '">' + esc(v || '?') + '</span>';
    }

    // Render one donor card. `c` is a normalized candidate (`_kind` = 'local' | 'api'); `i` its index in `all`.
    // Local candidates carry their full OEM list inline; API (extended-search) ones show the ONE matched OEM
    // and lazy-load the rest via the /donor-candidates/oems endpoint on "Show all OEMs".
    function donorCardHtml(c, i) {
        const wrongType = c.auto_pick_eligible === false;
        const already = c.item_code ? '<span class="dc-chip dc-chip-code" title="donor is already a SAP item">' + esc(c.item_code) + '</span>' : '';
        const apiBadge = c._kind === 'api' ? '<span class="dc-chip dc-chip-api" title="from extended TecDoc search">extended</span>' : '';
        const imgBox = c.image_url
            ? '<div class="donor-img"><img src="' + esc(c.image_url) + '" alt="" onerror="this.parentNode.classList.add(\'noimg\');this.remove();"/></div>'
            : '<div class="donor-img noimg"></div>';
        const kit = c.is_kit ? '<span class="pill pill-blue">KIT</span>' : '';

        const bits = [];
        if (c.part_component) bits.push(esc(c.part_component));
        if (c.spec_count) bits.push(c.spec_count + ' specs');
        if (c.compatible_vehicles_count) bits.push(c.compatible_vehicles_count + ' vehicles');
        if (c.categories_count) bits.push(c.categories_count + ' cat');
        const meta = bits.length ? '<div class="dc-meta">' + bits.join(' &middot; ') + '</div>' : '';

        // OEM block — the priority signal.
        let oemBlock;
        if (c._kind === 'api' && !c._oemsLoaded) {
            // Only the connecting OEM is free; the rest is one lazy fetch away.
            const matched = c.matched_oem
                ? '<span class="oem-chip">' + esc(c.matched_oem) + '</span>'
                : '<span class="dc-dim">—</span>';
            const shared = (c.shared_oem_count && c.shared_oem_count > 1)
                ? '<span class="oem-more">+' + (c.shared_oem_count - 1) + ' shared</span>' : '';
            const expand = c.tecdoc_article_id
                ? '<button type="button" class="dc-oem-expand" data-idx="' + i + '">Show all OEMs ▾</button>' : '';
            oemBlock = '<div class="dc-oems"><span class="dc-oems-lbl">Matched OEM</span>' +
                       '<span class="dc-oems-wrap" data-oemwrap="' + i + '">' + matched + shared + '</span>' + expand + '</div>';
        } else {
            const oemArr = c.oem_numbers || [];
            const oemTotal = (typeof c.oem_count === 'number') ? c.oem_count : oemArr.length;
            let oemHtml = oemArr.length
                ? oemArr.map(o => '<span class="oem-chip">' + esc(o) + '</span>').join('')
                : '<span class="dc-dim">none</span>';
            if (oemArr.length && oemTotal > oemArr.length) oemHtml += '<span class="oem-more">+' + (oemTotal - oemArr.length) + ' more</span>';
            oemBlock = '<div class="dc-oems"><span class="dc-oems-lbl">OEM (' + oemTotal + ')</span>' +
                       '<span class="dc-oems-wrap">' + oemHtml + '</span></div>';
        }

        // Aftermarket cross-references — secondary. null ⇒ unknown (show nothing).
        const xref = (c.crossref_count === null || c.crossref_count === undefined) ? ''
            : '<div class="dc-xref">' + Number(c.crossref_count).toLocaleString() + ' aftermarket cross-refs</div>';

        const vCls = c.verdict === 'MATCH' ? 'v-match' : (c.verdict === 'DIFFERENT' ? 'v-diff' : 'v-unc');
        return '<label class="dc' + (wrongType ? ' dc-wrong' : '') + '">' +
                '<input type="radio" name="donor-pick" value="' + i + '"' + (c.is_default ? ' checked' : '') + '>' +
                imgBox +
                '<div class="dc-main">' +
                    '<div class="dc-top">' +
                        '<span class="dc-art">' + esc(c.article_number) + '</span>' +
                        (c.is_default ? '<span class="dc-cur">current</span>' : '') + already + apiBadge +
                        '<span class="dc-verdict ' + vCls + '">' + esc(c.verdict || '?') + '</span>' +
                        (wrongType ? '<span class="dc-hand" title="different part type — override only">✋</span>' : '') + kit +
                    '</div>' +
                    '<div class="dc-name">' + esc(c.name) + ' <span class="dc-sup">— ' + esc(c.supplier) + '</span></div>' +
                    meta + oemBlock + xref +
                '</div>' +
            '</label>';
    }

    async function openDonorSwapModal(row, lineId) {
        let cands;
        try {
            const r = await fetch(base + '/lines/' + lineId + '/donor-candidates', { headers: API_KEY ? { 'X-Api-Key': API_KEY } : {} });
            if (!r.ok) throw new Error('HTTP ' + r.status);
            cands = await r.json();
        } catch (e) { alert('Could not load donor candidates: ' + e.message); return; }

        // No stored alternatives (e.g. the part came from a TecDoc search or Germax rather than an OEM
        // bridge): open the window anyway and run the TecDoc search straight away.
        const searchNow = !cands || !cands.length;

        // Unified working set. Local candidates first; extended-search (API) ones appended on demand.
        const all = (cands || []).map(c => Object.assign({ _kind: 'local' }, c));

        function wireOemExpanders(scope) {
            scope.querySelectorAll('.dc-oem-expand').forEach(btn => {
                btn.onclick = async (ev) => {
                    ev.preventDefault(); ev.stopPropagation();   // don't toggle the radio
                    const idx = parseInt(btn.dataset.idx, 10);
                    const c = all[idx];
                    if (!c || c._oemsLoaded) return;
                    btn.disabled = true; btn.textContent = 'Loading…';
                    try {
                        const res = await api('POST', '/lines/' + lineId + '/donor-candidates/oems',
                            { tecdoc_article_id: c.tecdoc_article_id });
                        const arr = (res && res.oem_numbers) || [];
                        c.oem_numbers = arr;
                        c.oem_count = (res && typeof res.oem_count === 'number') ? res.oem_count : arr.length;
                        if (res && res.crossref_count !== undefined) c.crossref_count = res.crossref_count;
                        c._oemsLoaded = true;
                        const wrap = scope.querySelector('[data-oemwrap="' + idx + '"]');
                        if (wrap) {
                            let html = arr.length ? arr.map(o => '<span class="oem-chip">' + esc(o) + '</span>').join('')
                                                  : '<span class="dc-dim">none</span>';
                            if (arr.length && c.oem_count > arr.length) html += '<span class="oem-more">+' + (c.oem_count - arr.length) + ' more</span>';
                            wrap.innerHTML = html;
                        }
                        const lbl = btn.parentNode && btn.parentNode.querySelector('.dc-oems-lbl');
                        if (lbl) lbl.textContent = 'OEM (' + c.oem_count + ')';
                        btn.remove();
                    } catch (e) {
                        btn.disabled = false; btn.textContent = 'Show all OEMs ▾';
                        alert('Could not load OEMs: ' + e.message);
                    }
                };
            });
        }

        function renderList() {
            const listEl = mBody.querySelector('#donor-list');
            if (!listEl) return;
            listEl.innerHTML = all.length
                ? all.map((c, i) => donorCardHtml(c, i)).join('')
                : '<p class="dc-dim">No stored alternatives for this line. Use “Find more candidates” to search TecDoc by its OEM numbers.</p>';
            wireOemExpanders(listEl);
        }

        const bodyHtml =
            '<h3 class="dc-title">Choose borrowed donor</h3>' +
            '<div class="donor-head-note">Pick the article whose part best matches this line — OEM overlap ' +
                'is the strongest signal. The new SAP item keeps its own freshly-minted code; only the borrowed ' +
                'specs/OEMs change. Not seeing the right one? Run an extended TecDoc search.</div>' +
            '<div class="donor-more-bar">' +
                '<button type="button" id="donor-find-more" class="btn btn-sm">🔍 Find more candidates</button>' +
                '<span id="donor-more-status" class="dc-dim"></span>' +
            '</div>' +
            '<div class="donor-list" id="donor-list"></div>';

        openModal(bodyHtml, [
            { label: 'Cancel', onClick: closeModal },
            { label: 'Use selected donor', primary: true, onClick: async () => {
                const sel = mBody.querySelector('input[name="donor-pick"]:checked');
                if (!sel) { alert('Select a donor first.'); return; }
                const c = all[parseInt(sel.value, 10)];
                if (!c) { alert('Select a donor first.'); return; }
                if (c.auto_pick_eligible === false &&
                    !confirm('This candidate is a different part type (' + (c.verdict || '') + '). Borrow from it anyway?')) return;
                try {
                    // Extended-search candidates aren't in oitm yet — the DGX materializes them on swap.
                    const res = c._kind === 'api'
                        ? await api('POST', '/lines/' + lineId + '/swap-donor-api',
                            { tecdoc_article_id: c.tecdoc_article_id, article_number: c.article_number,
                              supplier: c.supplier, supplier_id: c.supplier_id, name: c.name, verdict: c.verdict })
                        : await api('POST', '/lines/' + lineId + '/swap-donor',
                            { article_number: c.article_number, supplier: c.supplier });
                    closeModal();
                    if (res && res.review_status) setRowPill(row, res.review_status);
                    // Force the expanded enrichment panel to reload with the new donor.
                    const detail = detailRowFor(lineId);
                    if (detail) { const box = detail.querySelector('.ld-enrichment'); if (box) box.dataset.loaded = 'false'; }
                    await refreshSummary();
                } catch (e) { alert('Swap failed: ' + e.message); }
            } }
        ]);
        if (mBox) mBox.classList.add('modal-wide');   // this modal is a wide table

        renderList();

        // "Find more candidates" — broad DGX pool, deduped against the local list by tecdoc id / (article,supplier).
        const findBtn = mBody.querySelector('#donor-find-more');
        const statusEl = mBody.querySelector('#donor-more-status');
        if (findBtn) findBtn.onclick = async () => {
            findBtn.disabled = true; findBtn.textContent = 'Searching…'; if (statusEl) statusEl.textContent = '';
            try {
                const res = await api('POST', '/lines/' + lineId + '/donor-candidates/search', {});
                if (!res || res.available === false) {
                    if (statusEl) statusEl.textContent = (res && res.error) ? ('No more found (' + res.error + ')') : 'No more found.';
                    return;
                }
                const seenTid = new Set(all.filter(c => c.tecdoc_article_id).map(c => String(c.tecdoc_article_id)));
                const seenArt = new Set(all.map(c => (c.article_number || '').toLowerCase() + '|' + (c.supplier || '').toLowerCase()));
                let added = 0;
                (res.candidates || []).forEach(c => {
                    const tid = c.tecdoc_article_id ? String(c.tecdoc_article_id) : null;
                    const artKey = (c.article_number || '').toLowerCase() + '|' + (c.supplier || '').toLowerCase();
                    if ((tid && seenTid.has(tid)) || (c.article_number && seenArt.has(artKey))) return;   // already in list
                    if (tid) seenTid.add(tid);
                    seenArt.add(artKey);
                    all.push(Object.assign({ _kind: 'api' }, c, { is_default: false }));   // never steal the current selection
                    added++;
                });
                renderList();
                if (statusEl) statusEl.textContent = added
                    ? ('+' + added + ' more from extended search')
                    : 'No new candidates beyond the list.';
            } catch (e) {
                if (statusEl) statusEl.textContent = 'Search failed: ' + e.message;
            } finally {
                findBtn.disabled = false; findBtn.textContent = '🔍 Find more candidates';
            }
        };
        if (searchNow && findBtn) findBtn.click();
    }

    // ---- per-line actions (Match / Create New+enrich / Skip) ----
    document.querySelectorAll('#lines-table .actions-menu .menu button').forEach(btn => {
        btn.addEventListener('click', async () => {
            const row = btn.closest('tr');
            const lineId = row.dataset.lineId;
            const action = btn.dataset.action;
            const details = btn.closest('details'); if (details) details.open = false;
            try {
                if (action === 'automatch') {
                    const updated = await api('POST', '/lines/' + lineId + '/auto-match');
                    if (updated) setRowPill(row, updated.review_status);
                } else if (action === 'match') {
                    const sku = prompt('Manual match — enter the SAP item code (verified against the Autohub company):');
                    if (!sku) return;
                    const updated = await api('POST', '/lines/' + lineId + '/match', { item_code: sku });
                    if (updated) setRowPill(row, updated.review_status);
                } else if (action === 'skip') {
                    const updated = await api('POST', '/lines/' + lineId + '/skip');
                    if (updated) setRowPill(row, updated.review_status);
                } else if (action === 'reopen') {
                    const updated = await api('POST', '/lines/' + lineId + '/reopen');
                    if (updated) setRowPill(row, updated.review_status);
                } else if (action === 'confirm') {
                    openConfirmModal(row, lineId);
                } else if (action === 'create_new') {
                    // Enrichment already ran at extraction — Create New just marks the line for SAP
                    // creation (no re-enrichment). The explicit choice also confirms it for the gate.
                    const updated = await api('POST', '/lines/' + lineId + '/create-new', { confirmed: true });
                    if (updated) setRowPill(row, updated.review_status);
                } else if (action === 'swap_donor') {
                    await openDonorSwapModal(row, lineId);
                } else if (action === 'reenrich') {
                    await createNewFlow(row, lineId);   // explicit re-enrichment (rarely needed)
                } else if (action === 'needs_manual') {
                    const updated = await api('POST', '/lines/' + lineId + '/needs-manual');
                    if (updated) setRowPill(row, updated.review_status);
                }
                await refreshSummary();
            } catch (e) {
                alert('Action failed: ' + e.message);
            }
        });
    });

    // The actions menu is position:fixed (escapes the table's clipping/overflow), so we place it against
    // the viewport on open: right-aligned to the ⋮, dropping down or flipping up by available space, and
    // clamped inside the window so it's never cut off — short table or last row. Keep only one open.
    function placeActionsMenu(menu, sum) {
        const r = sum.getBoundingClientRect();
        const mh = menu.offsetHeight, mw = menu.offsetWidth;
        const spaceBelow = window.innerHeight - r.bottom;
        const openUp = (mh + 8 > spaceBelow) && (r.top - 8 > spaceBelow);   // flip up only if there's more room above
        let left = Math.min(r.right - mw, window.innerWidth - mw - 8);
        menu.style.left = Math.max(8, left) + 'px';
        const top = openUp ? (r.top - mh - 4) : (r.bottom + 4);
        menu.style.top = Math.max(8, Math.min(top, window.innerHeight - mh - 8)) + 'px';
    }
    document.querySelectorAll('#lines-table .actions-menu').forEach(details => {
        details.addEventListener('toggle', () => {
            if (!details.open) return;
            document.querySelectorAll('#lines-table .actions-menu[open]').forEach(o => { if (o !== details) o.open = false; });
            const menu = details.querySelector('.menu'), sum = details.querySelector('summary');
            if (menu && sum) placeActionsMenu(menu, sum);
        });
    });
    // A fixed menu doesn't move with the page — close it if the user scrolls or resizes while it's open.
    ['scroll', 'resize'].forEach(evt => window.addEventListener(evt, () => {
        document.querySelectorAll('#lines-table .actions-menu[open]').forEach(o => { o.open = false; });
    }, true));

    // ---- expandable line detail + inline edit ----
    function detailRowFor(lineId) {
        return document.querySelector('#lines-table tr.detail-row[data-line-id="' + lineId + '"]');
    }

    document.querySelectorAll('#lines-table tr.line-row').forEach(row => {
        row.addEventListener('click', async (e) => {
            if (e.target.closest('[data-noexpand]')) return;   // clicks in the Actions menu don't expand
            const lineId = row.dataset.lineId;
            const detail = detailRowFor(lineId);
            if (!detail) return;
            const show = detail.style.display === 'none';
            detail.style.display = show ? '' : 'none';
            row.classList.toggle('expanded', show);
            if (show) await loadEnrichment(lineId, detail);
        });
    });

    async function loadEnrichment(lineId, detail) {
        const box = detail.querySelector('.ld-enrichment');
        if (!box || box.dataset.loaded === 'true') return;
        box.dataset.loaded = 'true';
        try {
            const r = await fetch(base + '/lines/' + lineId + '/enrichment', { headers: API_KEY ? { 'X-Api-Key': API_KEY } : {} });
            if (r.status === 204) { box.innerHTML = '<em>No enrichment data (matched directly or not yet enriched).</em>'; return; }
            if (!r.ok) throw new Error('HTTP ' + r.status);
            const reviewStatus = document.querySelector('#lines-table tr.line-row[data-line-id="' + lineId + '"]')?.dataset.reviewStatus || '';
            const enr = await r.json();
            GROUP_NAMES = await loadGroupNames();
            box.innerHTML = enrichmentHtml(enr, reviewStatus, lineId);
            wireMarqueResolve(lineId, box);
            wireMarqueOverride(lineId, box);
            wireBorrowConfirm(lineId, box);
            wirePartReview(lineId, box);
        } catch (e) {
            box.dataset.loaded = 'false';
            box.innerHTML = '<em class="ld-error">Could not load enrichment: ' + esc(e.message) + '</em>';
        }
    }

    // Resolve a held line's marque: POST the operator's choice, then flip the row to its new status.
    function wireMarqueResolve(lineId, box) {
        const btn = box.querySelector('.mq-resolve');
        if (!btn) return;
        // Typing an override auto-selects its radio, so the operator doesn't have to click both.
        // Radios are scoped to this box (unique per-line group name) — query by type, not by shared name.
        const free = box.querySelector('.mq-freetext');
        const other = box.querySelector('input[type="radio"][value="__other__"]');
        if (free && other) free.addEventListener('input', () => { if (free.value.trim()) other.checked = true; });
        btn.addEventListener('click', async () => {
            // Confirm (radio = the DGX prefix), override (radio = __other__ → free text), or pick (a candidate radio).
            let code = '';
            const checked = box.querySelector('input[type="radio"]:checked');
            if (checked && checked.value === '__other__') code = (box.querySelector('.mq-freetext')?.value || '').trim();
            else if (checked) code = (checked.value || '').trim();
            else code = (box.querySelector('.mq-freetext')?.value || '').trim();
            const status = box.querySelector('.mq-status');
            if (!code) { if (status) status.textContent = 'Pick a marque first.'; return; }
            btn.disabled = true;
            if (status) status.textContent = 'Resolving…';
            try {
                // Body keys are snake_case to match the API's SnakeCaseLower JSON policy (server binds
                // ManufacturerCode from manufacturer_code) — a camelCase key deserializes to null.
                const updated = await api('POST', '/lines/' + lineId + '/resolve-manufacturer', { manufacturer_code: code });
                const row = document.querySelector('#lines-table tr.line-row[data-line-id="' + lineId + '"]');
                if (row) setRowPill(row, (updated && updated.reviewStatus) || 'create_new');
                // Re-render the panel from the server so the SKU-prefix row shows the resolved marque (not
                // the stale 'GEN') and the now-resolved picker drops away.
                box.dataset.loaded = 'false';
                await loadEnrichment(lineId, detailRowFor(lineId));
                refreshSummary();
            } catch (e) {
                if (status) status.textContent = 'Failed: ' + e.message;
                btn.disabled = false;
            }
        });
    }

    // Override an already-resolved line's marque and/or SAP item group, then re-render. Posts to the same
    // resolve-manufacturer endpoint (marque re-resolved via DGX + optional operator group override); the
    // line stays create_new so it mints on the next Bulk Create.
    function wireMarqueOverride(lineId, box) {
        const toggle = box.querySelector('.ov-toggle');
        if (!toggle) return;
        const form = box.querySelector('.ov-form');
        toggle.addEventListener('click', () => { form.style.display = (form.style.display === 'none') ? '' : 'none'; });
        const save = box.querySelector('.ov-save');
        save.addEventListener('click', async () => {
            const marque = (box.querySelector('.ov-marque')?.value || '').trim();
            const groupRaw = (box.querySelector('.ov-group')?.value || '').trim();
            const status = box.querySelector('.ov-status');
            if (!marque) { if (status) status.textContent = 'Enter a marque.'; return; }
            const body = { manufacturer_code: marque };
            if (groupRaw) {
                const g = parseInt(groupRaw, 10);
                if (!isFinite(g) || g <= 0) { if (status) status.textContent = 'Item group must be a positive number.'; return; }
                body.items_group_code = g;
            }
            save.disabled = true;
            if (status) status.textContent = 'Saving…';
            try {
                const updated = await api('POST', '/lines/' + lineId + '/resolve-manufacturer', body);
                const row = document.querySelector('#lines-table tr.line-row[data-line-id="' + lineId + '"]');
                if (row) setRowPill(row, (updated && updated.reviewStatus) || 'create_new');
                box.dataset.loaded = 'false';
                await loadEnrichment(lineId, detailRowFor(lineId));
                refreshSummary();
            } catch (e) {
                if (status) status.textContent = 'Failed: ' + e.message;
                save.disabled = false;
            }
        });
    }

    // Per-line sign-off for a cross-supplier borrow: marks the line confirmed (stays create_new) so Bulk
    // Create's borrow gate passes — the per-line equivalent of "Confirm all & Create".
    function wirePartReview(lineId, box) {
        // "Check the part" box: choose another part (donor window), or confirm the matched part is right.
        // Confirming stamps the line's enrichment confirmation (same as Confirm borrow) and queues it to
        // create, which also clears the ⚠ check-part flag.
        const choose = box.querySelector('.ld-choose-donor');
        if (choose) choose.addEventListener('click', () => {
            const row = document.querySelector('#lines-table tr.line-row[data-line-id="' + lineId + '"]');
            openDonorSwapModal(row, lineId);
        });
        const ok = box.querySelector('.ld-confirm-part');
        if (!ok) return;
        const status = box.querySelector('.cp-status');
        ok.addEventListener('click', async () => {
            ok.disabled = true;
            if (status) status.textContent = 'Confirming…';
            try {
                const updated = await api('POST', '/lines/' + lineId + '/create-new', { confirmed: true });
                const row = document.querySelector('#lines-table tr.line-row[data-line-id="' + lineId + '"]');
                if (row) {
                    setRowPill(row, (updated && updated.reviewStatus) || 'create_new');
                    row.querySelectorAll('.status-cell .pill-orange').forEach(p => { if (p.textContent.includes('check part')) p.remove(); });
                }
                if (status) status.textContent = '✓ Confirmed: will be created on Bulk Create.';
                refreshSummary();
            } catch (e) {
                if (status) status.textContent = 'Failed: ' + e.message;
                ok.disabled = false;
            }
        });
    }

    function wireBorrowConfirm(lineId, box) {
        const btn = box.querySelector('.ld-confirm-borrow');
        if (!btn) return;
        const status = box.querySelector('.cb-status');
        btn.addEventListener('click', async () => {
            btn.disabled = true;
            if (status) status.textContent = 'Confirming…';
            try {
                const updated = await api('POST', '/lines/' + lineId + '/create-new', { confirmed: true });
                const row = document.querySelector('#lines-table tr.line-row[data-line-id="' + lineId + '"]');
                if (row) setRowPill(row, (updated && updated.reviewStatus) || 'create_new');
                if (status) status.textContent = '✓ Confirmed — will create on Bulk Create.';
                refreshSummary();
            } catch (e) {
                if (status) status.textContent = 'Failed: ' + e.message;
                btn.disabled = false;
            }
        });
    }

    document.querySelectorAll('#lines-table .ld-save').forEach(btn => {
        btn.addEventListener('click', async () => {
            const detail = btn.closest('tr.detail-row');
            const lineId = detail.dataset.lineId;
            const qty = parseFloat(detail.querySelector('.ld-qty').value);
            const price = parseFloat(detail.querySelector('.ld-price').value);
            const body = {
                quantity: isFinite(qty) ? qty : null,
                unit_price_foreign: isFinite(price) ? price : null,
                description: detail.querySelector('.ld-desc').value
            };
            btn.disabled = true;
            try {
                const updated = await api('PATCH', '/lines/' + lineId, body);
                const mainRow = document.querySelector('#lines-table tr.line-row[data-line-id="' + lineId + '"]');
                if (mainRow && updated) {
                    const tds = mainRow.querySelectorAll('td');
                    // cols: 0 # · 1 art · 2 oems · 3 desc · 4 brand · 5 qty · 6 unit price · 7 line total · 8 status
                    tds[3].textContent = updated.description || '';
                    tds[5].innerHTML = (updated.quantity != null ? Number(updated.quantity).toLocaleString(undefined, { maximumFractionDigits: 3 }) : '')
                        + ' <span class="unit">' + esc(updated.unit || '') + '</span>';
                    tds[6].textContent = updated.unit_price_foreign != null ? fmtMoney(updated.unit_price_foreign) : '';
                    tds[7].textContent = updated.line_total_foreign != null ? fmtMoney(updated.line_total_foreign) : '';
                }
                const saved = detail.querySelector('.ld-saved');
                if (saved) { saved.style.display = ''; setTimeout(() => { saved.style.display = 'none'; }, 2000); }
                inlineDirty = false;   // edits persisted
            } catch (e) { alert('Save failed: ' + e.message); }
            finally { btn.disabled = false; }
        });
    });

    // ---- needs_confirmation: vehicle-group brand, operator picks use-existing / create-new / skip ----
    function openConfirmModal(row, lineId) {
        const tds = row.querySelectorAll('td');
        const article = tds[1].textContent.trim();
        const oems = (tds[2].getAttribute('title') || tds[2].textContent).trim();
        const brand = tds[4].textContent.trim();
        const code = row.dataset.suggestedCode || '';
        const sup = row.dataset.suggestedSupplier || '';
        const src = row.dataset.source || '';

        let html = '<h3>Confirm match — vehicle-group brand</h3>' +
            '<p>The invoice brand is a group code, not a supplier, so this match needs your confirmation.</p>' +
            '<table class="kv">' +
            '<tr><th>Brand</th><td>' + esc(brand) + '</td></tr>' +
            '<tr><th>Article</th><td>' + esc(article) + '</td></tr>' +
            '<tr><th>OEMs</th><td>' + esc(oems) + '</td></tr>' +
            '</table>';
        if (code) {
            html += '<p style="margin-top:8px;">Suggested existing SAP item: <strong>' + esc(code) + '</strong>' +
                (sup ? ' — ' + esc(sup) : '') + (src ? ' <span class="hint">(source: ' + esc(src) + ')</span>' : '') + '</p>';
        }

        const act = async (path, body, label) => {
            try {
                const u = await api('POST', '/lines/' + lineId + path, body);
                if (u) setRowPill(row, u.review_status);
                closeModal();
                await refreshSummary();
            } catch (e) { alert(label + ' failed: ' + e.message); }
        };

        const actions = [{ label: 'Cancel', onClick: closeModal }];
        if (code) actions.push({ label: 'Use ' + code, primary: true, onClick: () => act('/match', { item_code: code }, 'Match') });
        actions.push({ label: 'Create new', onClick: () => act('/create-new', { confirmed: true }, 'Create new') });
        actions.push({ label: 'Skip', onClick: () => act('/skip', undefined, 'Skip') });
        openModal(html, actions);
    }

    async function createNewFlow(row, lineId) {
        // 1) enrich; 2) auto-matched to an existing SAP item (Path C1) -> show, no create;
        //    3) failed/unmatched -> needs_manual; 4) borrowed -> confirm/reject modal;
        //    5) auto-accepted -> mark create_new.
        let resp;
        try { resp = await api('POST', '/lines/' + lineId + '/enrich'); }
        catch (e) {
            // The server has already flagged the line 'needs manual' on a hard failure.
            setRowPill(row, 'needs_manual');
            alert('Enrichment failed: ' + e.message + '\nLine flagged "needs manual".');
            await refreshSummary();
            return;
        }
        // New shape: { enrichment, routing, matched_item_code }. Tolerate the old flat shape too.
        const enr = resp.enrichment || resp;

        // Path C1 — the donor part is already a SAP item; the server auto-matched. No creation.
        if (resp.routing === 'auto_matched') {
            setRowPill(row, 'matched');
            openModal(
                '<h3>Auto-matched via OEM bridge</h3><p>This line matches an existing SAP item <strong>' +
                esc(resp.matched_item_code) + '</strong> (the donor part is already in SAP), so no new item ' +
                'will be created.</p>',
                [{ label: 'OK', primary: true, onClick: closeModal }]);
            await refreshSummary();
            return;
        }

        const confirmCreate = async () => {
            const updated = await api('POST', '/lines/' + lineId + '/create-new', { confirmed: true });
            if (updated) setRowPill(row, updated.review_status);
            closeModal();
            await refreshSummary();
        };
        const rejectManual = async () => {
            const updated = await api('POST', '/lines/' + lineId + '/reject');
            if (updated) setRowPill(row, updated.review_status);
            closeModal();
            await refreshSummary();
        };

        // No usable enrichment (failed / partial / unmatched) — server set needs_manual; tell operator.
        if (enr.status === 'failed' || !enr.item_data) {
            setRowPill(row, 'needs_manual');
            openModal(
                '<h3>No automatic enrichment</h3><p>This line could not be enriched automatically (' +
                esc(enr.status || enr.enrichment_source || enr.source || 'unmatched') +
                '). It is flagged <strong>needs manual</strong> — match it to an existing SAP item or skip it.</p>',
                [{ label: 'Close', primary: true, onClick: closeModal }]);
            await refreshSummary();
            return;
        }

        if (enr.confirmation_required) {
            openModal(enrichmentHtml(enr), [
                { label: 'Reject (needs manual)', onClick: () => rejectManual().catch(e => alert('Failed: ' + e.message)) },
                { label: 'Confirm & mark Create New', primary: true, onClick: () => confirmCreate().catch(e => alert('Failed: ' + e.message)) }
            ]);
        } else {
            // Auto-accepted (e.g. tecdoc_direct) — mark create_new without a prompt.
            await confirmCreate();
        }
    }

    // ---- banner buttons ----
    let lastAutoMatch = 0;
    document.getElementById('btn-automatch')?.addEventListener('click', async (e) => {
        const now = Date.now();
        if (now - lastAutoMatch < 30000) { alert('Please wait a moment before auto-matching again.'); return; }
        lastAutoMatch = now;
        e.target.disabled = true;
        try {
            const r = await api('POST', '/auto-match');
            alert('Auto-match: ' + r.newly_matched + ' newly matched, ' + r.still_pending + ' still pending. Reloading.');
            reloadKeepingPlace();
        } catch (err) { alert('Auto-match failed: ' + err.message); e.target.disabled = false; }
    });

    document.getElementById('btn-markpending')?.addEventListener('click', async () => {
        try { await api('POST', '/bulk-mark-pending-as-create-new'); reloadKeepingPlace(); }
        catch (err) { alert('Failed: ' + err.message); }
    });

    (function () {
        const btn = document.getElementById('btn-confirm-marques');
        if (!btn) return;
        btn.addEventListener('click', () => {
            openModal(
                '<h3>Confirm DGX-resolved marques</h3>' +
                '<p>Accept DGX\'s resolved marque for <strong>every auto-resolved line</strong> and queue them as Create New. ' +
                'It applies DGX\'s answer <strong>as-is</strong>.</p>' +
                '<p class="hint">Do this only <strong>after</strong> you\'ve spot-checked a sample and overridden any wrong ones ' +
                '(open the line → change the marque). These brands are multi-marque, so a few may be BMW/Mercedes, not VAG.</p>',
                [
                    { label: 'Cancel', onClick: closeModal },
                    { label: 'Confirm resolved', primary: true, onClick: async () => {
                        try {
                            const r = await api('POST', '/confirm-resolved-marques');
                            closeModal();
                            alert('Confirmed ' + (r.confirmed || 0) + ' resolved marque(s) — queued as Create New.');
                            reloadKeepingPlace();
                        } catch (e) { alert('Failed: ' + e.message); }
                    } }
                ]);
        });
    })();

    document.getElementById('btn-reopen-skipped')?.addEventListener('click', () => {
        const n = document.getElementById('cnt-reopen-skipped').textContent;
        const reopen = async (q) => {
            try { await api('POST', '/bulk-reopen-skipped' + q); reloadKeepingPlace(); }
            catch (e) { alert('Reopen failed: ' + e.message); }
        };
        openModal(
            '<h3>Reopen skipped lines</h3>' +
            '<p>Move <strong>' + n + '</strong> skipped line(s) back into review:</p>' +
            '<ul style="margin:6px 0 0 18px;">' +
            '<li><strong>Needs manual</strong> — you decide each line (match / create / skip).</li>' +
            '<li><strong>Re-run enrichment</strong> — re-query DGX in the background, then auto-route ' +
            '(promotional lines stay skipped). Lines show as <em>pending</em> until the worker finishes.</li>' +
            '</ul>',
            [
                { label: 'Cancel', onClick: closeModal },
                { label: '→ Needs manual', onClick: () => reopen('') },
                { label: 'Re-run enrichment', primary: true, onClick: () => reopen('?reEnrich=true') }
            ]);
    });

    // Bulk Create, Confirm all & Create, Skip All Pending and Complete Review moved to the Create & finish tab
    // (/autohub/documents/{id}/finish, wwwroot/js/autohub/finish.js).

    // Self-managing poll: refreshSummary() arms/disarms this via ensurePolling() based on whether there's
    // live work. Each tick skips the fetch while the tab is hidden (no server load on a backgrounded tab),
    // so we no longer poll review-summary forever on an idle, fully-settled document.
    let pollTimer = null;
    function ensurePolling(live) {
        if (live && pollTimer === null) {
            pollTimer = setInterval(() => { if (!document.hidden) refreshSummary(); }, 10000);
        } else if (!live && pollTimer !== null) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }
    // Re-check immediately when the tab becomes visible again (so a settled→live change isn't missed).
    document.addEventListener('visibilitychange', () => { if (!document.hidden) refreshSummary(); });

    // Deep link from the Issues / Prices pages: #line-<lineId> expands and scrolls to that line;
    // #line-<lineId>/donor also opens its donor window.
    function openFromHash() {
        const m = /^#line-([0-9a-f-]{36})(\/donor)?$/i.exec(location.hash || '');
        if (!m) return;
        const row = document.querySelector('#lines-table tr.line-row[data-line-id="' + m[1] + '"]');
        if (!row) return;
        const detail = detailRowFor(m[1]);
        if (detail && detail.style.display === 'none') row.click();
        row.scrollIntoView({ block: 'center' });
        row.classList.remove('flash'); void row.offsetWidth; row.classList.add('flash');
        if (m[2]) openDonorSwapModal(row, m[1]);
    }
    openFromHash();
    window.addEventListener('hashchange', openFromHash);

    // Used by lines-tools.js (search / filters / bulk) to update rows in place after a bulk action.
    window.invoiceRefreshSummary = refreshSummary;
    window.invoiceSetRowPill = setRowPill;

    refreshSummary();   // initial load; arms the poll iff there's live work
})();

// ---- delete invoice (works in any document state, so it lives outside the review IIFE) ----
(function () {
    const btn = document.getElementById('btn-delete-doc');
    if (!btn) return;
    const docId = btn.dataset.docId;
    const apiKey = document.querySelector('meta[name="api-key"]')?.content || '';
    btn.addEventListener('click', async () => {
        if (!confirm('Delete this invoice and all its extracted lines? This cannot be undone. (Any SAP items already created are NOT affected.)')) return;
        btn.disabled = true;
        try {
            const r = await fetch('/api/autohub/documents/' + docId, {
                method: 'DELETE',
                headers: apiKey ? { 'X-Api-Key': apiKey } : {}
            });
            if (!r.ok) { throw new Error((await r.text()) || ('HTTP ' + r.status)); }
            window.location.href = '/autohub/documents';
        } catch (e) { alert('Delete failed: ' + e.message); btn.disabled = false; }
    });
})();

// ---- re-extract Excel in place (re-parse the stored .xlsx, refresh the lines) ----
(function () {
    const btn = document.getElementById('btn-reextract-doc');
    if (!btn) return;
    const docId = btn.dataset.docId;
    const apiKey = document.querySelector('meta[name="api-key"]')?.content || '';
    const original = btn.textContent;
    btn.addEventListener('click', async () => {
        if (!confirm('Re-parse the stored Excel file and replace these extracted lines? '
            + 'Any per-line review edits on this document will be discarded and re-enrichment will re-run. '
            + '(SAP items already created are NOT affected.)')) return;
        btn.disabled = true;
        btn.textContent = 'Re-extracting…';
        try {
            const r = await fetch('/api/autohub/documents/' + docId + '/re-extract', {
                method: 'POST',
                headers: apiKey ? { 'X-Api-Key': apiKey } : {}
            });
            if (!r.ok) { throw new Error((await r.text()) || ('HTTP ' + r.status)); }
            reloadKeepingPlace();
        } catch (e) {
            alert('Re-extract failed: ' + e.message);
            btn.disabled = false;
            btn.textContent = original;
        }
    });
})();
