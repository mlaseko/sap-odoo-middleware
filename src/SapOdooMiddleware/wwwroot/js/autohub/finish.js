// Create & finish tab (/autohub/documents/{id}/finish): exactly what Bulk Create would do now
// (GET .../finish-summary), then Bulk Create (background job, progress shown) and Complete Review.
// The irreversible actions live only here, each behind a confirmation that repeats the numbers.
(function () {
    const root = document.getElementById('fi-root');
    if (!root) return;
    const docId = root.dataset.docId;
    const base = '/api/autohub/documents/' + docId;
    const page = '/autohub/documents/' + docId;
    const API_KEY = document.querySelector('meta[name="api-key"]')?.content || '';
    const body = document.getElementById('fi-body');
    const msg = document.getElementById('fi-message');
    let summary = null;
    let monitoring = null;

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
    const esc = s => (s == null ? '' : String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])));
    function showMessage(html, kind) {
        msg.innerHTML = html ? '<div class="' + (kind === 'error' ? 'note-error' : 'notice') + '">' + html + '</div>' : '';
    }
    function fixLink(c) {
        if (c.issues_group === 'price') return ' <a href="' + page + '/prices">Price review →</a>';
        if (c.issues_group) return ' <a href="' + page + '/issues#group=' + c.issues_group + '">Fix in Issues →</a>';
        return '';
    }
    const list = items => items.length
        ? '<ul>' + items.map(c => '<li><strong>' + c.count + '</strong> · ' + esc(c.title) + fixLink(c) + '</li>').join('') + '</ul>'
        : '';

    async function refreshTabs() {
        try {
            const d = await api('GET', '/issues');
            if (window.updateInvoiceTabs) window.updateInvoiceTabs(d.summary);
        } catch (_) { }
    }

    async function load() {
        try { summary = await api('GET', '/finish-summary'); }
        catch (e) { body.innerHTML = '<div class="note-error">Could not load the summary: ' + esc(e.message) + '</div>'; return; }
        render();
        if (summary.running_job_id && !monitoring) monitor(summary.running_job_id, null);
    }

    function render() {
        const s = summary;
        const editable = s.document_status === 'extracted';
        const running = !!(s.running_job_id || monitoring);
        let html = '';
        if (!editable)
            html += '<div class="notice">This invoice is <strong>' + esc(s.document_status) + '</strong>: nothing more to create or complete.</div>';

        html += '<div class="fi-grid">';

        // 1. Will be created now.
        html += '<div class="fi-card go"><h3>Will be created now</h3><div class="big">' + s.will_create + '</div>' +
            '<div class="sub">items, when you run Bulk Create.</div>' + list(s.will_create_by);
        if (s.part_not_checked > 0)
            html += '<p class="sub">⚠ ' + s.part_not_checked + ' of them have a matched part that was never confirmed. ' +
                '<a href="' + page + '/issues#group=check_part">Check them first →</a></p>';
        if (s.marque_not_confirmed > 0)
            html += '<p class="sub">⚠ ' + s.marque_not_confirmed + ' of them will be created under a marque nothing confirms. ' +
                '<a href="' + page + '/issues#group=confirm_marque">Confirm them first →</a></p>';
        html += '<p class="sub">' + (s.require_price_review
            ? 'Price review is required: only approved lines are created.'
            : 'Lines approved in Price review use their approved PL03; held lines are skipped; lines not reviewed use the calculated PL03.') + '</p>';
        if (editable) html += '<div class="fi-actions"><button type="button" class="btn btn-primary" id="fi-create"' +
            (s.will_create && !running ? '' : ' disabled') + '>' + (running ? 'Bulk Create is running…' : 'Create ' + s.will_create + ' item(s) in SAP') + '</button></div>';
        html += '<div id="fi-run"></div></div>';

        // 2. Queued but not created yet.
        html += '<div class="fi-card' + (s.not_created ? ' warn' : '') + '"><h3>Queued, but will not be created yet</h3><div class="big">' + s.not_created + '</div>' +
            '<div class="sub">These stay queued until fixed.</div>' + list(s.not_created_by) + '</div>';

        // 3. Not queued.
        html += '<div class="fi-card' + (s.still_open ? ' warn' : '') + '"><h3>Not decided yet</h3><div class="big">' + s.still_open + '</div>' +
            '<div class="sub">Lines not queued, matched or skipped. Bulk Create does not touch them.</div>' + list(s.still_open_by);
        if (editable && s.skippable_open > 0)
            html += '<div class="fi-actions"><button type="button" class="btn btn-danger btn-sm" id="fi-skip-open">Skip ' + s.skippable_open +
                ' undecided line(s)</button><span class="sub">pending and needs-manual lines; you can reopen them later.</span></div>';
        html += '</div>';

        // 4. Complete review.
        html += '<div class="fi-card' + (s.can_complete ? ' go' : '') + '"><h3>Complete the review</h3>' +
            '<div class="sub">' + s.total + ' lines: <span class="fi-done">' + s.created + ' created</span> · ' + s.matched + ' matched · ' + s.skipped + ' skipped</div>';
        if (s.can_complete) {
            html += '<p>Every line is created, matched or skipped.</p>' +
                '<div class="fi-actions"><button type="button" class="btn btn-primary" id="fi-complete">Complete review</button></div>';
        } else if (editable) {
            html += '<p class="sub">Complete Review needs every line created, matched or skipped. Still open:</p>' + list(s.complete_blockers);
        }
        html += '</div>';

        html += '</div>';
        body.innerHTML = html;
    }

    // ---- Bulk Create ----
    async function runCreate() {
        const s = summary;
        const lines = s.will_create_by.map(c => '  ' + c.count + ' · ' + c.title).join('\n');
        const not = s.not_created ? '\n\n' + s.not_created + ' queued line(s) will NOT be created yet.' : '';
        if (!confirm('Create ' + s.will_create + ' item(s) in SAP?\n\n' + lines + not + '\n\nThis cannot be undone.')) return;
        showMessage('');
        let r;
        try { r = await api('POST', '/bulk-create-async'); }
        catch (e) { showMessage('Bulk Create could not start: ' + esc(e.message), 'error'); return; }
        monitor(r.job_id, s.will_create);
    }

    async function monitor(jobId, target) {
        monitoring = jobId;
        render();
        let baseCreated = summary.created;
        const tick = async () => {
            const run = document.getElementById('fi-run');
            try {
                const rs = await api('GET', '/review-summary');
                const done = Math.max(0, ((rs.by_status || {}).created || 0) - baseCreated);
                if (run) run.innerHTML = '<div class="fi-progress"><div style="width:' + (target ? Math.min(100, Math.round(done / target * 100)) : 50) + '%"></div></div>' +
                    '<div class="sub">Created ' + done + (target ? ' of ' + target : '') + '… It keeps running if you leave this page.</div>';
            } catch (_) { }
            let job;
            try { job = await api('GET', '/bulk-create-job/' + jobId); } catch (_) { return setTimeout(tick, 2500); }
            if (job.status === 'running') return setTimeout(tick, 2000);

            monitoring = null;
            if (job.status === 'failed') {
                showMessage('Bulk Create failed: ' + esc(job.error || 'unknown error'), 'error');
            } else {
                let html = '<strong>Bulk Create finished:</strong> ' + job.created + ' of ' + job.attempted + ' created';
                if (job.failed) html += ', ' + job.failed + ' failed';
                if (job.needs_confirmation) html += ', ' + job.needs_confirmation + ' need confirmation';
                if (job.held) html += ', ' + job.held + ' held (marque or SKU range)';
                if (job.not_price_approved || job.price_held) html += ', ' + (job.not_price_approved || 0) + ' not price-approved, ' + (job.price_held || 0) + ' held in price review';
                html += '.';
                if (job.failures && job.failures.length)
                    html += '<ul>' + job.failures.slice(0, 20).map(f => '<li>' + esc(f.article_number || '?') + ': ' + esc(f.error) + '</li>').join('') + '</ul>' +
                        (job.failures.length > 20 ? '<p>…and ' + (job.failures.length - 20) + ' more.</p>' : '') +
                        '<p><a href="' + page + '/issues#group=create_failed">Retry failed lines in Issues →</a></p>';
                showMessage(html, job.failed && !job.created ? 'error' : null);
            }
            await load();
            refreshTabs();
        };
        tick();
    }

    // ---- other actions ----
    async function skipOpen() {
        const n = summary.skippable_open;
        if (!confirm('Skip ' + n + ' undecided line(s) (pending and needs manual)? They will not be created or matched. You can reopen them from All lines.')) return;
        try { await api('POST', '/bulk-skip-pending'); showMessage('Skipped the undecided lines.'); }
        catch (e) { showMessage('Could not skip: ' + esc(e.message), 'error'); }
        await load();
        refreshTabs();
    }

    async function complete() {
        if (!confirm('Complete the review of this invoice? Lines can no longer be changed afterwards.')) return;
        try { await api('POST', '/complete-review'); location.href = '/autohub/documents'; }
        catch (e) { showMessage('Cannot complete the review: ' + esc(e.message), 'error'); await load(); }
    }

    body.addEventListener('click', ev => {
        if (ev.target.id === 'fi-create') runCreate();
        else if (ev.target.id === 'fi-skip-open') skipOpen();
        else if (ev.target.id === 'fi-complete') complete();
    });

    load();
})();
