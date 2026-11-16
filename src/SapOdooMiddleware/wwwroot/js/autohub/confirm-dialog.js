// Themed confirmation dialog for the Autohub pages, in place of the browser's confirm(): same look as the
// other modals (.modal-overlay / .modal in site.css). Usage:
//   if (!await window.ahConfirm({ title: 'Create the PO?', rows: [['Vendor', 'VEN1011']], note: '…', ok: 'Create', danger: false })) return;
// Resolves true on the OK button, false on Cancel, Escape or a click outside the dialog.
(function () {
    const esc = s => (s == null ? '' : String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])));

    window.ahConfirm = function (opts) {
        const o = opts || {};
        return new Promise(resolve => {
            const overlay = document.createElement('div');
            overlay.className = 'modal-overlay';
            let html = '<div class="modal" role="dialog" aria-modal="true"><h3>' + esc(o.title || 'Are you sure?') + '</h3>';
            if (o.message) html += '<p>' + esc(o.message) + '</p>';
            if (o.rows && o.rows.length)
                html += o.rows.map(r => '<div class="kv"><span class="k">' + esc(r[0]) + '</span><span class="v">' +
                    esc(r[1]) + '</span></div>').join('');
            if (o.list && o.list.length)
                html += '<ul>' + o.list.map(x => '<li>' + esc(x) + '</li>').join('') + '</ul>';
            if (o.note) html += '<p class="sub">' + esc(o.note) + '</p>';
            html += '<div class="modal-actions"><button type="button" class="btn" data-ah="cancel">' + esc(o.cancel || 'Cancel') +
                '</button><button type="button" class="btn ' + (o.danger ? 'btn-danger' : 'btn-primary') + '" data-ah="ok">' +
                esc(o.ok || 'OK') + '</button></div></div>';
            overlay.innerHTML = html;

            const done = result => {
                document.removeEventListener('keydown', onKey, true);
                overlay.remove();
                resolve(result);
            };
            const onKey = ev => {
                if (ev.key === 'Escape') { ev.preventDefault(); done(false); }
                else if (ev.key === 'Enter') { ev.preventDefault(); done(true); }
            };
            overlay.addEventListener('click', ev => {
                if (ev.target === overlay) done(false);
                const act = ev.target.closest && ev.target.closest('[data-ah]');
                if (act) done(act.dataset.ah === 'ok');
            });
            document.addEventListener('keydown', onKey, true);
            document.body.appendChild(overlay);
            overlay.querySelector('[data-ah="ok"]').focus();
        });
    };
})();
