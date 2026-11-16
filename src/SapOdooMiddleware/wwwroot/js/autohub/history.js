// History tab (/autohub/documents/{id}/history): the invoice's activity log, newest first, with search.
(function () {
    const root = document.getElementById('hi-root');
    if (!root) return;
    const docId = root.dataset.docId;
    const API_KEY = document.querySelector('meta[name="api-key"]')?.content || '';
    const tbody = document.querySelector('#hi-table tbody');
    const search = document.getElementById('hi-search');
    const count = document.getElementById('hi-count');
    const esc = s => (s == null ? '' : String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])));
    let entries = [];

    function render() {
        const q = search.value.trim().toLowerCase();
        const shown = entries.filter(e => !q || [e.actor, e.detail, e.action, e.line_number].join(' ').toLowerCase().includes(q));
        count.textContent = shown.length === entries.length ? entries.length + ' entries' : shown.length + ' of ' + entries.length + ' entries';
        tbody.innerHTML = shown.length ? shown.map(e =>
            '<tr><td style="white-space:nowrap">' + esc(new Date(e.at).toLocaleString()) + '</td>' +
            '<td>' + esc(e.actor) + '</td>' +
            '<td class="num">' + (e.line_id
                ? '<a href="/autohub/documents/' + docId + '#line-' + e.line_id + '">' + esc(e.line_number ?? '?') + '</a>'
                : '<span class="sub">invoice</span>') + '</td>' +
            '<td>' + esc(e.detail) + '</td></tr>').join('')
            : '<tr><td colspan="4" class="sub">' + (entries.length ? 'Nothing matches the search.' : 'No history yet. Changes made from now on are recorded here.') + '</td></tr>';
    }

    fetch('/api/autohub/documents/' + docId + '/activity?limit=2000', { headers: API_KEY ? { 'X-Api-Key': API_KEY } : {} })
        .then(r => r.ok ? r.json() : Promise.reject(new Error('HTTP ' + r.status)))
        .then(list => { entries = list || []; render(); })
        .catch(e => { tbody.innerHTML = '<tr><td colspan="4" class="note-error">Could not load the history (' + esc(e.message) +
            '). If this is the first time, run migrations/2026-11-17__invoice_activity.sql on the Autohub Neon database.</td></tr>'; });
    search.addEventListener('input', render);
})();
