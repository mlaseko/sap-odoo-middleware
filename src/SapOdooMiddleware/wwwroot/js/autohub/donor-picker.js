// Shared "choose the part (donor)" window for Autohub invoice lines, used by the invoice page
// (invoice-lines.js) and the Issues tab (issues.js). Lists the line's stored alternatives, runs the extended
// TecDoc search ("Find more candidates", automatically when there are none) and swaps the line's part.
// The host page supplies its API helper and modal: AutohubDonorPicker({ api, esc, openModal, closeModal,
// mBody, mBox, base, apiKey }).open(lineId, onSwapped) — onSwapped(result) runs after a successful swap.
window.AutohubDonorPicker = function (host) {
    const { api, esc, openModal, closeModal, mBody, mBox, base } = host;
    const API_KEY = host.apiKey || '';

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

    async function open(lineId, onSwapped) {
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
                    if (onSwapped) await onSwapped(res);
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

    return { open };
};
