// Autohub price review page (/autohub/documents/{id}/prices). Page values come from data attributes on #pr-bar.
(function () {
    const bar = document.getElementById('pr-bar');
    const EDITABLE = bar.dataset.editable === 'true';
    const base = '/api/autohub/documents/' + bar.dataset.docId + '/price-review';
    const API_KEY = document.querySelector('meta[name="api-key"]')?.content || '';
    const body = document.getElementById('pr-body');
    const msg = document.getElementById('pr-message');

    let review = null;          // last GET /price-review
    let filter = 'all';
    const edits = {};           // lineId -> { pl03, reason, preview }
    const relCache = {};        // lineId -> comparables response
    const openRel = new Set();  // lineIds with the related-parts row open
    let relLoading = false;     // the related parts of every line are being read (page open)
    let relAutoOpened = false;  // lines with related parts were opened once; after that the reviewer decides

    async function api(method, path, payload) {
        const opts = { method, headers: {} };
        if (API_KEY) opts.headers['X-Api-Key'] = API_KEY;
        if (payload !== undefined) { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(payload); }
        const r = await fetch(base + path, opts);
        const text = await r.text();
        const json = text ? JSON.parse(text) : null;
        if (!r.ok) {
            const e = new Error((json && json.error) || ('HTTP ' + r.status));
            e.body = json;
            throw e;
        }
        return json;
    }

    const esc = s => (s == null ? '' : String(s).replace(/[&<>"]/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;' }[c])));
    const tzs = v => v == null ? '–' : Math.round(Number(v)).toLocaleString();
    const pct = v => v == null ? '–' : (Number(v) * 100).toFixed(1) + '%';
    const signedPct = v => v == null ? '–' : (v > 0 ? '+' : '') + (Number(v) * 100).toFixed(0) + '%';
    const money = (v, cur) => v == null ? '–' : Number(v).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + (cur ? ' ' + cur : '');
    const FLAG_CSS = { below_cost: 'pill-red', margin_low: 'pill-orange', margin_high: 'pill-yellow',
                       floor_bound: 'pill-grey', no_brand_ratio: 'pill-grey', above_related: 'pill-yellow',
                       below_related: 'pill-blue', cost_outlier: 'pill-red', same_article: 'pill-orange' };
    const FLAG_LABEL = { below_cost: 'below cost', margin_low: 'low margin', margin_high: 'high margin',
                         floor_bound: 'brand minimum', no_brand_ratio: 'default ratio', above_related: 'above related',
                         below_related: 'below related', cost_outlier: 'cost outlier', same_article: 'already in SAP?' };
    const flagsHtml = flags => (flags || []).map(f =>
        '<span class="pill flag ' + (FLAG_CSS[f.code] || 'pill-grey') + '" title="' + esc(f.text) + '">' + esc(FLAG_LABEL[f.code] || f.code) + '</span>').join('');

    function showMessage(html, kind) {
        msg.innerHTML = html ? '<div class="' + (kind === 'error' ? 'note-error' : 'notice') + '" style="margin-bottom:12px">' + html + '</div>' : '';
    }

    // ---- state helpers ----
    const lineById = id => review.lines.find(l => l.line_id === id);
    function decisionState(l) {
        const d = l.decision;
        if (!d) return 'todo';
        if (d.stale) return 'todo';
        return d.status;   // approved | held
    }
    function currentPl03(l) {                     // what the input shows
        const e = edits[l.line_id];
        if (e && e.pl03 !== undefined) return e.pl03;
        if (l.decision && !l.decision.stale && l.decision.final_pl03 != null) return l.decision.final_pl03;
        return l.formula_pl03;
    }
    const isChanged = l => l.formula_pl03 != null && currentPl03(l) != null && Number(currentPl03(l)) !== Number(l.formula_pl03);
    function hasUnsaved(l) {
        const e = edits[l.line_id];
        if (!e) return false;
        const saved = l.decision && !l.decision.stale ? l.decision.final_pl03 : l.formula_pl03;
        return (e.pl03 !== undefined && Number(e.pl03) !== Number(saved)) ||
               (e.reason !== undefined && (e.reason || '') !== ((l.decision && l.decision.reason) || ''));
    }

    // ---- money box ----
    const grouped = n => Number(n).toLocaleString('en-US', { maximumFractionDigits: 0 });
    const digitsOf = s => String(s || '').replace(/[^0-9]/g, '');

    // ---- OEM numbers and vehicle fitment (side panel) ----
    const OEMS_ON_LINE = 3;
    function oemLine(l) {
        const oems = l.oem_numbers || [];
        if (!oems.length) return '';
        const more = oems.length - OEMS_ON_LINE;
        return '<div class="oem-line">OEM ' + oems.slice(0, OEMS_ON_LINE).map(esc).join(', ') +
            (more > 0 ? ' <a class="fit-open" data-fit="' + l.line_id + '">+' + more + ' more</a>' : '') + '</div>';
    }

    // Vehicle makes a fitment text can name (two-word ones first, so "LAND ROVER" wins over a single word).
    const KNOWN_MAKES = ['ALFA ROMEO', 'ASTON MARTIN', 'LAND ROVER', 'MERCEDES-BENZ', 'ROLLS-ROYCE',
        'VW', 'VOLKSWAGEN', 'AUDI', 'SKODA', 'SEAT', 'CUPRA', 'PORSCHE', 'BMW', 'MINI', 'JAGUAR', 'VOLVO', 'FORD', 'OPEL',
        'VAUXHALL', 'PEUGEOT', 'CITROEN', 'CITROËN', 'RENAULT', 'DACIA', 'NISSAN', 'INFINITI', 'TOYOTA', 'LEXUS', 'HONDA',
        'MAZDA', 'MITSUBISHI', 'SUBARU', 'SUZUKI', 'HYUNDAI', 'KIA', 'FIAT', 'JEEP', 'CHRYSLER', 'DODGE', 'CHEVROLET',
        'SAAB', 'ISUZU', 'IVECO', 'BENTLEY', 'MASERATI', 'TESLA'];
    const reEsc = s => s.replace(/[.*+?^${}()|[\]\\-]/g, '\\$&');
    function makeOf(vehicle) {
        const v = vehicle.toUpperCase();
        return KNOWN_MAKES.find(m => v === m || v.startsWith(m + ' ')) || v.split(/\s+/)[0];
    }

    // One vehicle per entry: a new vehicle starts at a known make, at the text's first word, or at any word
    // that starts a vehicle after ")" at least twice (as on the Issues tab).
    function splitFitment(text) {
        const t = String(text || '').trim();
        if (!t) return [];
        const makes = new Set([makeOf(t), ...KNOWN_MAKES]);
        const after = {};
        (t.match(/\)\s+[A-Z][A-Z-]{2,}(?=\s)/g) || []).forEach(m => {
            const w = m.replace(/^\)\s+/, '');
            after[w] = (after[w] || 0) + 1;
        });
        Object.keys(after).forEach(w => { if (after[w] >= 2) makes.add(w); });
        const re = new RegExp('\\s+(?=(?:' + [...makes].map(reEsc).join('|') + ')\\s)', 'g');
        return t.split(re).map(x => x.trim()).filter(Boolean);
    }
    const fitCache = {};
    function vehiclesOf(l) {
        if (!(l.line_id in fitCache)) {
            const groups = new Map();
            splitFitment(l.fit_for_auto).forEach(v => {
                const make = makeOf(v);
                if (!groups.has(make)) groups.set(make, []);
                groups.get(make).push(v.slice(make.length).trim() || v);
            });
            fitCache[l.line_id] = groups;
        }
        return fitCache[l.line_id];
    }
    // "LAND ROVER" → "Land Rover", "MERCEDES-BENZ" → "Mercedes-Benz"; short makes (VW, BMW, KIA) stay as they are.
    const titleCase = s => s.length <= 3 ? s : s.toLowerCase().replace(/(^|[\s-])\p{L}/gu, c => c.toUpperCase());
    function fitPill(l) {
        const groups = vehiclesOf(l);
        const n = [...groups.values()].reduce((a, v) => a + v.length, 0);
        if (!n) return '';
        const makes = [...groups.keys()];
        return '<button type="button" class="fit-pill fit-open" data-fit="' + l.line_id + '" title="Show every vehicle and OEM number">' +
            '<b>' + n + ' vehicle' + (n === 1 ? '' : 's') + '</b> · ' + esc(makes.slice(0, 4).map(titleCase).join(', ')) +
            (makes.length > 4 ? ' +' + (makes.length - 4) : '') + ' ›</button>';
    }

    let fitPanel = null;
    function closeFit() {
        if (!fitPanel) return;
        fitPanel.remove(); fitPanel = null;
        document.removeEventListener('keydown', fitKey, true);
    }
    function fitKey(ev) { if (ev.key === 'Escape') { ev.preventDefault(); closeFit(); } }
    function openFit(lineId) {
        const l = lineById(lineId);
        if (!l) return;
        closeFit();
        const groups = vehiclesOf(l);
        const n = [...groups.values()].reduce((a, v) => a + v.length, 0);
        const oems = l.oem_numbers || [];
        fitPanel = document.createElement('aside');
        fitPanel.className = 'fit-panel';
        fitPanel.setAttribute('role', 'dialog');
        fitPanel.innerHTML =
            '<button type="button" class="fit-close" title="Close (Esc)" aria-label="Close">✕</button>' +
            '<h3>' + (n ? 'Fits ' + n + ' vehicle' + (n === 1 ? '' : 's') : 'Vehicle fitment') + '</h3>' +
            '<div class="sub">Line ' + l.line_number + ' · ' + esc(l.description || '') + ' · ' + esc(l.article_number || '') + (l.brand ? ' ' + esc(l.brand) : '') + '</div>' +
            (oems.length ? '<div class="fit-mk">OEM numbers (' + oems.length + ')</div><div class="fit-oems">' + oems.map(o => '<span>' + esc(o) + '</span>').join('') + '</div>' : '') +
            (n ? '<input type="search" class="fit-filter" placeholder="Filter: model, engine or year">' +
                 [...groups.entries()].map(([mk, vs]) =>
                    '<div class="fit-group"><div class="fit-mk">' + esc(mk) + ' (' + vs.length + ')</div>' +
                    vs.map(v => '<div class="fit-v">' + esc(v) + '</div>').join('') + '</div>').join('')
               : '<p class="sub">No vehicle fitment for this part.</p>');
        document.body.appendChild(fitPanel);
        document.addEventListener('keydown', fitKey, true);
        fitPanel.querySelector('.fit-close').addEventListener('click', closeFit);
        const filter = fitPanel.querySelector('.fit-filter');
        if (filter) {
            filter.addEventListener('input', () => {
                const q = filter.value.trim().toLowerCase();
                fitPanel.querySelectorAll('.fit-group').forEach(g => {
                    const mk = g.querySelector('.fit-mk').textContent.toLowerCase();
                    let shown = 0;
                    g.querySelectorAll('.fit-v').forEach(v => {
                        const hit = !q || mk.includes(q) || v.textContent.toLowerCase().includes(q);
                        v.style.display = hit ? '' : 'none';
                        if (hit) shown++;
                    });
                    g.style.display = shown ? '' : 'none';
                });
            });
            filter.focus();
        }
    }
    document.addEventListener('click', ev => {
        if (fitPanel && !fitPanel.contains(ev.target) && !ev.target.closest('.fit-open')) closeFit();
    });

    // ---- render ----
    function renderCounts() {
        const s = review.summary;
        document.getElementById('pr-counts').innerHTML =
            '<span class="rc">' + s.total + '</span> lines to create · ' +
            '<span class="rc">' + s.approved + '</span> approved · ' +
            '<span class="rc">' + s.held + '</span> held · ' +
            '<span class="rc">' + s.unreviewed + '</span> to review' + (s.stale ? ' (' + s.stale + ' changed since approval)' : '') + ' · ' +
            '<span class="rc">' + s.flagged + '</span> with warnings' +
            (s.errors ? ' · <span class="rc err">' + s.errors + ' cannot be priced</span>' : '');
        const cur = review.currency || '?';
        document.getElementById('pr-currency').textContent = review.forex_rate
            ? cur + ' at ' + Number(review.forex_rate).toLocaleString() + ' TZS'
            : (review.forex_error || cur);
    }

    function visible(l) {
        switch (filter) {
            case 'todo': return decisionState(l) === 'todo';
            case 'flagged': return (l.flags || []).length > 0 || !!l.error;
            case 'approved': return decisionState(l) === 'approved';
            case 'held': return decisionState(l) === 'held';
            default: return true;
        }
    }

    function decisionCell(l) {
        const d = l.decision;
        let html = '';
        if (d) {
            const pill = d.status === 'approved' ? 'pill-green' : 'pill-yellow';
            html += '<span class="pill ' + pill + '">' + esc(d.status) + '</span>' +
                    '<div class="sub">' + esc(d.reviewed_by) + ' · ' + new Date(d.reviewed_at).toLocaleString() + '</div>';
            if (d.stale) html += '<div class="stale" title="' + esc((d.stale_reasons || []).join(' ')) + '">⚠ figures changed: review again</div>';
        }
        if (EDITABLE && !l.error) {
            html += '<div class="pr-actions" style="margin-top:4px">' +
                '<button class="btn btn-sm btn-primary" data-act="approved" type="button">Approve</button>' +
                '<button class="btn btn-sm" data-act="held" type="button">Hold</button>' +
                (d ? '<button class="btn btn-sm" data-act="clear" type="button" title="Clear the decision">Clear</button>' : '') +
                '</div>';
        }
        return html;
    }

    function rowHtml(l) {
        const e = edits[l.line_id] || {};
        const pv = e.preview;
        const pl03 = currentPl03(l);
        const pl05 = pv ? pv.pl05 : (l.decision && !l.decision.stale && l.decision.final_pl05 != null ? l.decision.final_pl05 : l.formula_pl05);
        const margin = pv ? pv.margin : (l.decision && !l.decision.stale && l.decision.final_margin != null ? l.decision.final_margin : l.formula_margin);
        const flags = pv ? pv.flags : l.flags;
        const changed = isChanged(l);
        const reason = e.reason !== undefined ? e.reason : ((l.decision && l.decision.reason) || '');
        const discount = l.discount_pct ? '<div class="sub">less ' + Number(l.discount_pct) + '% = ' + money(l.effective_unit_price) + '</div>' : '';
        // vs the calculated PL03, shown under the box once it is changed.
        const vsCalc = changed && Number(l.formula_pl03) > 0
            ? '<div class="money-note">was ' + tzs(l.formula_pl03) + ' · ' + signedPct(Number(pl03) / Number(l.formula_pl03) - 1) + '</div>' : '';
        const pl03Cell = l.error
            ? '–'
            : (EDITABLE
                ? '<label class="money' + (changed ? ' changed' : '') + '"><span>TZS</span>' +
                  '<input class="pl03" type="text" inputmode="numeric" autocomplete="off" value="' + esc(pl03 != null ? grouped(Math.round(pl03)) : '') + '"></label>' +
                  vsCalc +
                  (changed ? '<input class="reason" type="text" maxlength="500" placeholder="Reason for the change (required)" value="' + esc(reason) + '">' : '')
                : tzs(pl03) + vsCalc + (reason ? '<div class="sub">' + esc(reason) + '</div>' : ''));
        return '<tr class="pr-row' + (changed ? ' changed' : '') + (l.error ? ' line-error' : '') + '" data-line-id="' + l.line_id + '">' +
            '<td class="num">' + l.line_number + '</td>' +
            '<td class="desc">' + (l.picture_url
                ? '<span class="ln-pic"><img class="ln-thumb" loading="lazy" alt="" title="Click to enlarge" src="' + esc(l.picture_url) +
                  '" data-zoom="' + esc(l.picture_url) + '" onerror="this.parentElement.remove()"></span>'
                : '') +
                '<div>' + esc(l.description || '') + '</div>' +
                '<div class="sub">' + esc(l.article_number || '') + (l.brand ? ' · ' + esc(l.brand) : '') + '</div>' +
                oemLine(l) + fitPill(l) +
                (l.error ? '<div class="err">' + esc(l.error) + '</div>' : '') + '</td>' +
            '<td class="num">' + (l.quantity != null ? Number(l.quantity) : '–') + '</td>' +
            '<td class="num">' + money(l.unit_price_foreign, review.currency) + discount + '</td>' +
            '<td class="num">' + tzs(l.cif_tzs) + '<div class="sub">PL01 ' + tzs(l.pl01) + '</div></td>' +
            '<td class="num" title="' + esc(l.ratio_brand ? 'ratio ' + l.ratio + ' (' + l.ratio_brand + ')' + (l.floor_applied ? ', brand minimum ' + tzs(l.floor) : '') : '') + '">' + tzs(l.formula_pl03) + '</td>' +
            '<td class="num">' + pl03Cell + '</td>' +
            '<td class="num">' + tzs(pl05) + '</td>' +
            '<td class="num">' + pct(margin) + '</td>' +
            '<td>' + flagsHtml(flags) + '</td>' +
            '<td>' + decisionCell(l) + '</td>' +
            '<td class="rel-cell">' + relSummaryCell(l) + '</td>' +
            '</tr>' +
            (openRel.has(l.line_id) ? '<tr class="rel-row" data-rel-for="' + l.line_id + '"><td colspan="12">' + relHtml(l) + '</td></tr>' : '');
    }

    function render() {
        renderCounts();
        const rows = review.lines.filter(visible);
        body.innerHTML = rows.length
            ? rows.map(rowHtml).join('')
            : '<tr><td colspan="12">' + (review.lines.length ? 'No lines match this filter.' : 'No lines are waiting to be created on this invoice.') + '</td></tr>';
    }

    function rerenderRow(lineId) {
        const l = lineById(lineId);
        const tr = body.querySelector('tr.pr-row[data-line-id="' + lineId + '"]');
        if (!l || !tr) return;
        const relTr = body.querySelector('tr.rel-row[data-rel-for="' + lineId + '"]');
        const focused = document.activeElement && tr.contains(document.activeElement) ? document.activeElement.className : null;
        const caret = focused ? document.activeElement.selectionStart : null;
        const tmp = document.createElement('tbody');
        tmp.innerHTML = rowHtml(l);
        if (relTr) relTr.remove();
        const nodes = Array.from(tmp.children);
        tr.replaceWith(...nodes);
        if (focused) {
            const el = nodes[0].querySelector('input.' + focused.split(' ')[0]);
            if (el) { el.focus(); try { el.setSelectionRange(caret, caret); } catch (_) { } }
        }
        renderCounts();
    }

    // ---- related parts ----
    // Where the PL03 in the box (typed, saved or calculated) sits against the related parts' median.
    function relPosition(l, c) {
        const typed = currentPl03(l);
        const ours = typed != null && Number(typed) > 0 ? Number(typed) : c.line_pl03;
        const median = c.same_stats ? c.same_stats.median : null;
        const vs = ours && median ? ours / median - 1 : null;
        const band = Number(c.comparable_band || 0.25);
        const pos = vs == null ? null : (vs > band ? 'above' : (vs < -band ? 'below' : 'in_line'));
        return { ours, median, vs, pos };
    }

    const relCount = c => ((c.same || []).length + (c.other_tier || []).length);

    // PL03 at the related parts' median, rounded like the calculated PL03 (the line's retail rounding step).
    function medianPl03(l, c) {
        const m = c && c.same_stats ? Number(c.same_stats.median) : null;
        if (!(m > 0)) return null;
        const step = Number(l.retail_rounding_step) > 0 ? Number(l.retail_rounding_step) : 1;
        return Math.max(step, Math.round(m / step) * step);
    }

    // "Use median" sets PL03 to the median; shown when the line is editable and is not already there.
    function medianButton(l, c) {
        if (!EDITABLE || l.error) return '';
        const target = medianPl03(l, c);
        if (target == null || Number(currentPl03(l)) === target) return '';
        return '<button class="btn btn-sm use-median" type="button" data-median="' + l.line_id + '" ' +
            'title="Set PL03 to ' + tzs(target) + ', the median of ' + c.same_stats.n + ' related part(s)">Use median ' + tzs(target) + '</button>';
    }

    function useMedian(lineId) {
        const l = lineById(lineId), c = relCache[lineId];
        const target = medianPl03(l, c);
        if (target == null) return;
        const e = edits[lineId] || (edits[lineId] = {});
        e.pl03 = target;
        e.preview = null;
        if (isChanged(l) && !(e.reason || '').trim())
            e.reason = 'Set to the median PL03 of ' + c.same_stats.n + ' related part(s): ' + tzs(c.same_stats.median) + '.';
        rerenderRow(lineId);
        schedulePreview(lineId);
    }

    // The Related column: median, count and position at a glance, and the toggle for the parts list.
    function relSummaryCell(l) {
        const c = relCache[l.line_id];
        const arrow = openRel.has(l.line_id) ? '▾' : '▸';
        if (!c) return relLoading ? '<span class="sub">Loading…</span>' : '<span class="rel-toggle" data-rel="1">' + arrow + ' Related</span>';
        if (c.error) return '<span class="err" title="' + esc(c.error) + '">Not available</span>';
        const n = relCount(c);
        if (!n) return '<span class="sub">No related parts</span>';
        const { vs, pos } = relPosition(l, c);
        let html = '<span class="rel-toggle" data-rel="1">' + arrow + ' ' + n + ' related</span>';
        if (c.same_stats)
            html += '<div class="sub">median ' + tzs(c.same_stats.median) + ' (' + c.same_stats.n + ')</div>' +
                    (vs != null ? '<div class="pos-' + esc(pos) + '"><strong>' + signedPct(vs) + '</strong> vs median</div>' : '') +
                    medianButton(l, c);
        else
            html += '<div class="sub">other tiers only</div>';
        const same = (c.flags || []).find(f => f.code === 'same_article');
        if (same) html += '<div>' + flagsHtml([same]) + '</div>';
        return html;
    }

    function relHtml(l) {
        const c = relCache[l.line_id];
        if (!c) return 'Loading related parts…';
        if (c.error) return '<span class="err">' + esc(c.error) + '</span>';
        // Compare the PL03 in the box (typed, saved or calculated), not only what the server last saw.
        const { ours, median, vs, pos } = relPosition(l, c);
        const ourLabel = isChanged(l) ? 'your PL03' : (c.line_pl03_source === 'decision' ? 'approved PL03' : 'calculated PL03');
        const flags = (c.flags || []).filter(f => f.code !== 'above_related' && f.code !== 'below_related');
        if (pos === 'above') flags.push({ code: 'above_related', text: 'PL03 is ' + Math.round(vs * 100) + '% above the median of ' + c.same_stats.n + ' related parts.' });
        if (pos === 'below') flags.push({ code: 'below_related', text: 'PL03 is ' + Math.round(-vs * 100) + '% below the median of ' + c.same_stats.n + ' related parts.' });
        let html = '<div class="rel-summary">';
        if (c.same_stats) {
            html += '<span>Related parts (' + esc(c.tier) + (c.tier_mapped ? '' : ', brand not mapped') + '): ' +
                '<span class="big">median ' + tzs(c.same_stats.median) + '</span> · range ' + tzs(c.same_stats.min) + '–' + tzs(c.same_stats.max) +
                ' · ' + c.same_stats.n + ' priced</span>';
            if (vs != null)
                html += '<span>' + esc(ourLabel) + ' ' + tzs(ours) + ' is <span class="big pos-' + esc(pos) + '">' +
                        signedPct(vs) + '</span> vs the median</span>';
            html += medianButton(l, c);
        } else {
            html += '<span>No priced related parts in the same tier.</span>';
        }
        if (c.part_name) html += '<span>Part: ' + esc(c.part_name) + '</span>';
        else html += '<span class="sub">Part name unknown: grouped by tier only.</span>';
        if (c.median_avg_cost != null) html += '<span>Median SAP average cost: ' + tzs(c.median_avg_cost) + ' (' + c.median_avg_cost_n + ' items)</span>';
        html += '</div>';
        if (flags.length) html += '<div style="margin-bottom:6px">' + flagsHtml(flags) + ' ' + flags.map(f => esc(f.text)).join(' ') + '</div>';
        if (c.note) html += '<div class="sub">' + esc(c.note) + '</div>';
        html += partsTable(c.same, ours);
        if ((c.other_tier || []).length) {
            html += '<details class="other-tiers"><summary>Other quality tiers (' + c.other_tier.length + ')' +
                (c.other_tier_stats ? ': median ' + tzs(c.other_tier_stats.median) : '') + '</summary>' +
                partsTable(c.other_tier, ours) + '</details>';
        }
        if (c.other_part_count) html += '<div class="sub">' + c.other_part_count + ' further part(s) share an OEM number but are a different part, so they are not compared.</div>';
        return html;
    }

    function partsTable(parts, ourPl03) {
        if (!parts || !parts.length) return '';
        return '<table class="rel"><thead><tr><th></th><th>Item</th><th>Part</th><th>Brand / tier</th><th>Article · supplier</th>' +
            '<th class="num">Stock</th><th class="num">PL03</th><th class="num">PL05</th><th class="num">Ours vs this</th><th>Shared OEMs</th></tr></thead><tbody>' +
            parts.map(p => '<tr>' +
                '<td>' + (p.image_url ? '<img loading="lazy" src="' + esc(p.image_url) + '" data-zoom="' + esc(p.image_url) +
                    '" alt="" title="Click to enlarge" onerror="this.remove()">' : '') + '</td>' +
                '<td>' + esc(p.item_code) + '</td>' +
                '<td>' + esc(p.name || p.item_name || '') + (p.part_component ? '<div class="sub">' + esc(p.part_component) + (p.vehicles_count ? ' · fits ' + p.vehicles_count + ' vehicles' : '') + '</div>' : '') + '</td>' +
                '<td>' + esc(p.brand || '') + '<div class="sub">' + esc(p.tier) + (p.tier_mapped ? '' : ' (not mapped)') + '</div></td>' +
                '<td>' + esc(p.article_number || '') +
                    (p.same_article ? ' ' + flagsHtml([{ code: 'same_article', text: 'Same supplier article as this invoice line: the part may already be in SAP.' }]) : '') +
                    '<div class="sub">' + esc(p.supplier_name || '') + '</div>' +
                    (p.catalogue_article_number ? '<div class="stale" title="The catalogue row linked to ' + esc(p.item_code) +
                        ' describes another part. Article and name shown are SAP\'s.">catalogue says ' + esc(p.catalogue_article_number) + '</div>' : '') +
                    '</td>' +
                '<td class="num">' + Number(p.on_hand || 0) + '</td>' +
                '<td class="num">' + tzs(p.pl03) + '</td>' +
                '<td class="num">' + tzs(p.pl05) + '</td>' +
                '<td class="num">' + (p.pl03 && ourPl03 ? signedPct(ourPl03 / p.pl03 - 1) : '–') + '</td>' +
                '<td class="sub">' + esc((p.shared_oems || []).join(', ')) + '</td>' +
                '</tr>').join('') +
            '</tbody></table>';
    }

    // Every line's related parts in one call when the page opens; lines with related parts open once.
    async function loadAllRelated() {
        relLoading = true;
        render();
        try {
            const r = await api('GET', '/comparables');
            (r.lines || []).forEach(c => { relCache[c.line_id] = c; });
            (r.errors || []).forEach(e => { relCache[e.line_id] = { error: e.error }; });
            review.lines.forEach(l => { if (!relCache[l.line_id]) relCache[l.line_id] = { same: [], other_tier: [], note: 'No related parts.' }; });
            if (!relAutoOpened) {
                review.lines.forEach(l => { const c = relCache[l.line_id]; if (c && !c.error && relCount(c)) openRel.add(l.line_id); });
                relAutoOpened = true;
            }
        } catch (e) {
            showMessage('Could not load the related parts: ' + esc(e.message) + '. Open them line by line with “Related”.', 'error');
        }
        relLoading = false;
        render();
    }

    function setAllRelated(open) {
        review.lines.forEach(l => {
            const c = relCache[l.line_id];
            if (open && c && !c.error && relCount(c)) openRel.add(l.line_id);
            if (!open) openRel.delete(l.line_id);
        });
        render();
    }

    async function loadRelated(lineId, force) {
        if (relCache[lineId] && !force) return;
        try { relCache[lineId] = await api('GET', '/lines/' + lineId + '/comparables'); }
        catch (e) { relCache[lineId] = { error: 'Could not load related parts: ' + e.message }; }
        rerenderRow(lineId);
    }

    // ---- live recalculation ----
    const timers = {};
    function schedulePreview(lineId) {
        clearTimeout(timers[lineId]);
        timers[lineId] = setTimeout(async () => {
            const e = edits[lineId];
            if (!e || e.pl03 === undefined || !(Number(e.pl03) > 0)) return;
            const asked = Number(e.pl03);
            try {
                const r = await api('POST', '/preview', { lines: [{ line_id: lineId, pl03: asked }] });
                // A slower answer for an earlier keystroke must not overwrite the figures of the PL03 now in the box.
                if (Number(e.pl03) !== asked) return;
                const p = r.lines[0];
                e.preview = p.error ? null : p;
                if (p.error) showMessage(esc(p.error), 'error');
            } catch (err) {
                if (Number(e.pl03) !== asked) return;
                showMessage('Could not recalculate: ' + esc(err.message), 'error');
            }
            rerenderRow(lineId);
        }, 300);
    }

    // ---- decisions ----
    async function decide(items) {
        showMessage('');
        try {
            const r = await api('POST', '/decisions', { lines: items });
            review = r.review;
            items.forEach(i => { delete edits[i.line_id]; delete relCache[i.line_id]; });
            render();
            openRel.forEach(id => loadRelated(id));
            showMessage('Saved ' + r.saved + ' decision(s).');
        } catch (e) {
            const errs = e.body && e.body.errors;
            if (errs && errs.length) {
                const line = id => { const l = lineById(id); return l ? 'Line ' + l.line_number : 'Request'; };
                showMessage('Nothing was saved:<ul>' + errs.map(x => '<li>' + esc(line(x.line_id)) + ': ' + esc(x.error) + '</li>').join('') + '</ul>', 'error');
            } else {
                showMessage('Could not save: ' + esc(e.message), 'error');
            }
        }
    }

    body.addEventListener('input', ev => {
        const tr = ev.target.closest('tr.pr-row');
        if (!tr) return;
        const id = tr.dataset.lineId;
        const e = edits[id] || (edits[id] = {});
        if (ev.target.classList.contains('pl03')) {
            // Accounting format as you type: keep only digits, regroup, and keep the caret after the same digit.
            const input = ev.target;
            const caretDigits = digitsOf(input.value.slice(0, input.selectionStart || 0)).length;
            const digits = digitsOf(input.value).replace(/^0+(?=\d)/, '');
            input.value = digits ? grouped(Number(digits)) : '';
            let pos = 0, seen = 0;
            while (pos < input.value.length && seen < caretDigits) { if (/\d/.test(input.value[pos])) seen++; pos++; }
            try { input.setSelectionRange(pos, pos); } catch (_) { }
            e.pl03 = digits === '' ? null : Number(digits);
            e.preview = null;
            const l = lineById(id);
            const nowChanged = isChanged(l);
            if (nowChanged !== tr.classList.contains('changed')) rerenderRow(id);   // show / hide the reason box
            schedulePreview(id);
        } else if (ev.target.classList.contains('reason')) {
            e.reason = ev.target.value;
        }
    });

    body.addEventListener('click', ev => {
        const med = ev.target.closest('button[data-median]');
        if (med) { useMedian(med.dataset.median); return; }
        const fit = ev.target.closest('.fit-open');
        if (fit) { openFit(fit.dataset.fit); return; }
        const tr = ev.target.closest('tr.pr-row');
        if (!tr) return;
        const id = tr.dataset.lineId;
        if (ev.target.closest('[data-rel]')) {
            if (openRel.has(id)) openRel.delete(id); else { openRel.add(id); loadRelated(id); }
            rerenderRow(id);
            return;
        }
        const btn = ev.target.closest('button[data-act]');
        if (!btn) return;
        const l = lineById(id);
        const act = btn.dataset.act;
        if (act === 'clear') {
            api('DELETE', '/decisions/' + id).then(reload).catch(e => showMessage('Could not clear: ' + esc(e.message), 'error'));
            return;
        }
        const pl03 = currentPl03(l);
        if (pl03 == null || !(Number(pl03) > 0)) { showMessage('Line ' + l.line_number + ': enter a PL03 above zero.', 'error'); return; }
        const e = edits[id] || {};
        const reason = e.reason !== undefined ? e.reason : (l.decision && l.decision.reason) || '';
        decide([{ line_id: id, status: act, pl03: isChanged(l) ? Number(pl03) : null, reason: isChanged(l) ? reason : null }]);
    });

    document.getElementById('pr-filter').addEventListener('click', ev => {
        const b = ev.target.closest('button[data-f]');
        if (!b) return;
        filter = b.dataset.f;
        document.querySelectorAll('#pr-filter button').forEach(x => x.classList.toggle('active', x === b));
        render();
    });

    document.getElementById('pr-rel-open').addEventListener('click', () => setAllRelated(true));
    document.getElementById('pr-rel-close').addEventListener('click', () => setAllRelated(false));

    const approveClean = document.getElementById('pr-approve-clean');
    if (approveClean) approveClean.addEventListener('click', () => {
        const items = review.lines
            .filter(l => !l.error && (l.flags || []).length === 0 && decisionState(l) !== 'approved'
                         && !hasUnsaved(l) && !isChanged(l))
            .map(l => ({ line_id: l.line_id, status: 'approved', pl03: null, reason: null }));
        if (!items.length) { showMessage('No unapproved lines without warnings at the calculated price.'); return; }
        if (!confirm('Approve ' + items.length + ' line(s) at their calculated PL03?')) return;
        decide(items);
    });

    async function reload() {
        try {
            review = await api('GET', '');
            render();
            if (!relAutoOpened) loadAllRelated();
        } catch (e) {
            body.innerHTML = '<tr><td colspan="12" class="err">Could not load the price review: ' + esc(e.message) + '</td></tr>';
        }
    }

    window.addEventListener('beforeunload', ev => {
        if (review && review.lines.some(hasUnsaved)) { ev.preventDefault(); ev.returnValue = ''; }
    });

    reload();
})();
