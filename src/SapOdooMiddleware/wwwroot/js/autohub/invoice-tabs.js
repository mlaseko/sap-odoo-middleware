// Invoice tab bar counts (Areas/Autohub/Pages/Documents/_InvoiceTabs.cshtml).
// Fetches GET /api/autohub/documents/{id}/issues once, unless the open page already has the data and
// calls window.updateInvoiceTabs(summary) itself (the Issues page does).
(function () {
    const nav = document.getElementById('inv-tabs');
    if (!nav) return;
    const API_KEY = document.querySelector('meta[name="api-key"]')?.content || '';

    function set(name, text) {
        const el = nav.querySelector('[data-count="' + name + '"]');
        if (!el) return;
        el.textContent = text;
        el.style.display = text === '' ? 'none' : 'inline-block';   // the stylesheet hides empty counts
    }

    window.updateInvoiceTabs = function (summary) {
        if (!summary) return;
        const by = summary.by_next_action || {};
        const price = by.price || 0;
        set('lines', String(summary.total));
        set('issues', (summary.with_issues - price) > 0 ? String(summary.with_issues - price) : '');
        set('prices', price > 0 ? String(price) : '');
        set('progress', summary.done + ' of ' + summary.total + ' done · ' + summary.ready + ' ready to create');
        set('finish', summary.ready > 0 ? String(summary.ready) : '');
    };

    if (nav.dataset.active === 'issues') return;   // the Issues page reports its own data
    fetch('/api/autohub/documents/' + nav.dataset.docId + '/issues', { headers: API_KEY ? { 'X-Api-Key': API_KEY } : {} })
        .then(r => r.ok ? r.json() : null)
        .then(d => d && window.updateInvoiceTabs(d.summary))
        .catch(() => { });
})();
