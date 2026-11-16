// Full-size picture viewer: click any image with data-zoom (the small part pictures on All lines, the picture
// in the line panel) to open it full size. Close with Esc, the ✕ button or a click on the dark background.
(function () {
    let overlay = null, lastFocus = null;

    function close() {
        if (!overlay) return;
        overlay.remove(); overlay = null;
        document.removeEventListener('keydown', onKey, true);
        if (lastFocus && lastFocus.focus) lastFocus.focus();
    }
    function onKey(e) { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(); } }

    function open(url) {
        close();
        lastFocus = document.activeElement;
        overlay = document.createElement('div');
        overlay.className = 'pic-zoom';
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.innerHTML = '<button type="button" class="pic-zoom-close" title="Close (Esc)" aria-label="Close">✕</button>' +
            '<img alt="Part picture">';
        const img = overlay.querySelector('img');
        img.src = url;
        img.onerror = () => { img.replaceWith(Object.assign(document.createElement('div'), { className: 'no-pic', textContent: 'Picture could not be loaded' })); };
        overlay.addEventListener('click', e => { if (e.target === overlay || e.target.closest('.pic-zoom-close')) close(); });
        document.body.appendChild(overlay);
        document.addEventListener('keydown', onKey, true);
        overlay.querySelector('.pic-zoom-close').focus();
    }

    document.addEventListener('click', e => {
        const img = e.target.closest && e.target.closest('img[data-zoom]');
        if (!img || img.closest('.pic-zoom')) return;
        e.preventDefault(); e.stopPropagation();
        open(img.dataset.zoom || img.src);
    }, true);

    window.AutohubPicZoom = { open, close };
})();
