// Shows that the app is working while a slow request runs (changing a line's part, bulk actions):
//   const done = AutohubBusy.start('Changing the part…', 'The part is looked up again; this can take up to a minute.');
//   ... await the request ...; done();
// start() puts a box with a spinner and the seconds elapsed at the bottom of the screen, visible wherever the
// page is scrolled. button(btn, 'Working…') disables a button and shows a spinner in it; it returns a restore().
window.AutohubBusy = (function () {
    let box = null, timer = null, depth = 0;

    function start(text, hint) {
        depth++;
        if (!box) {
            box = document.createElement('div');
            box.className = 'busy-toast';
            box.setAttribute('role', 'status');
            box.setAttribute('aria-live', 'polite');
            document.body.appendChild(box);
        }
        const t0 = Date.now();
        box.innerHTML = '<span class="busy-spin"></span><div><div class="busy-text"></div>' +
            (hint ? '<div class="busy-hint"></div>' : '') + '</div><span class="busy-secs"></span>';
        box.querySelector('.busy-text').textContent = text;
        if (hint) box.querySelector('.busy-hint').textContent = hint;
        box.style.display = 'flex';
        clearInterval(timer);
        const secs = box.querySelector('.busy-secs');
        timer = setInterval(() => { const s = Math.round((Date.now() - t0) / 1000); if (s >= 2) secs.textContent = s + ' s'; }, 500);
        let ended = false;
        return function done() {
            if (ended) return;
            ended = true;
            if (--depth > 0) return;
            depth = 0;
            clearInterval(timer);
            if (box) box.style.display = 'none';
        };
    }

    function button(btn, label) {
        if (!btn) return () => { };
        const html = btn.innerHTML, wasDisabled = btn.disabled;
        btn.disabled = true;
        btn.classList.add('is-busy');
        btn.innerHTML = '<span class="busy-spin small"></span>' + (label ? ' ' + label.replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c])) : html);
        return function restore() {
            if (!btn.isConnected) return;
            btn.innerHTML = html;
            btn.disabled = wasDisabled;
            btn.classList.remove('is-busy');
        };
    }

    return { start, button };
})();
