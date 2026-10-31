// JellyShelf reader — EPUB (reflowable text).
//
// PDFs and comics are raster pages drawn on a canvas; an EPUB is HTML laid out
// as flowing text, so it has its own reader built on the vendored foliate-js
// (MIT). This module is loaded on demand by reader.js when a book is an EPUB.
//
// foliate-js renders each chapter in a sandboxed same-origin iframe; reading
// preferences (font, size, line spacing, column width, justification, theme)
// are applied by injecting a stylesheet into every chapter with
// renderer.setStyles(). The bundled accessibility fonts (OpenDyslexic, Atkinson
// Hyperlegible) are declared there with absolute URLs to the plugin's /fonts.
//
// Preferences persist per-browser (localStorage); the last reading position is
// remembered per book as an EPUB CFI so reopening resumes where you left off.

import './vendor/foliate/view.js'; // registers the <foliate-view> element
import { makeStateClient } from './reader-state.js';

const PREFS_KEY = 'jellyshelf_epub_prefs';
const POS_PREFIX = 'jellyshelf_epub_pos_';
const PREFS_STATE_KEY = 'prefs/epub';

// Column widths the "Largeur" stepper cycles through (max-inline-size, px).
const WIDTHS = [480, 560, 640, 760, 900, 1200];
const WIDTH_LABELS = ['Très étroite', 'Étroite', 'Moyenne', 'Large', 'Très large', 'Pleine'];

const SIZE_MIN = 60;
const SIZE_MAX = 260;
const SIZE_STEP = 10;
const SPACING_MIN = 1.0;
const SPACING_MAX = 2.4;
const SPACING_STEP = 0.1;

const FONTS = {
    default: null,
    serif: 'Georgia, "Iowan Old Style", "Times New Roman", serif',
    sans: 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif',
    opendyslexic: '"OpenDyslexic", sans-serif',
    atkinson: '"Atkinson Hyperlegible", sans-serif',
};

const THEMES = {
    light: { bg: '#ffffff', fg: '#1a1a1a', link: '#0066cc' },
    sepia: { bg: '#f4ecd8', fg: '#5b4636', link: '#9b4a1b' },
    dark: { bg: '#181818', fg: '#cfcfcf', link: '#6cb6ff' },
};

const DEFAULTS = {
    font: 'default',
    size: 100,
    spacing: 1.5,
    width: 640,
    justify: true,
    hyphenate: true,
    theme: 'light',
    flow: 'paginated',
};

let view = null;
let prefs = { ...DEFAULTS };
let base = '';
let itemId = null;
let showFn = () => {};
let state = null;        // the per-account state client (prefs + position)
let currentCfi = null;   // most recent reading position, for a save on page close
let currentFraction = 0;

// --- Preferences (persisted per Jellyfin account, cached in localStorage) ---

function loadLocalPrefs() {
    try {
        return JSON.parse(localStorage.getItem(PREFS_KEY) || 'null');
    } catch {
        return null;
    }
}

async function loadPrefs() {
    const remote = await state.load(PREFS_STATE_KEY);
    if (remote) {
        return { ...DEFAULTS, ...remote };
    }
    // First time for this account: seed the account store from this browser's
    // own saved preferences, if any, so nothing is lost on the switch.
    const local = loadLocalPrefs();
    if (local) {
        state.save(PREFS_STATE_KEY, local, 0);
        return { ...DEFAULTS, ...local };
    }
    return { ...DEFAULTS };
}

function savePrefs() {
    try {
        localStorage.setItem(PREFS_KEY, JSON.stringify(prefs));
    } catch { /* storage full or disabled — the account copy still persists */ }
    state.save(PREFS_STATE_KEY, prefs);
}

async function loadPosition() {
    const remote = await state.load(`pos/${itemId}`);
    if (remote && remote.cfi) {
        return remote.cfi;
    }
    try {
        return localStorage.getItem(POS_PREFIX + itemId) || null;
    } catch {
        return null;
    }
}

function savePosition(cfi) {
    if (!cfi) {
        return;
    }
    try {
        localStorage.setItem(POS_PREFIX + itemId, cfi);
    } catch { /* ignore */ }
    state.save(`pos/${itemId}`, { cfi, fraction: currentFraction });
}

// --- Styling injected into each chapter ---

function fontFaces() {
    const dir = `${base}/JellyShelf/fonts`;
    const face = (family, file, weight, style) =>
        `@font-face{font-family:"${family}";font-weight:${weight};font-style:${style};`
        + `font-display:swap;src:url("${dir}/${file}.woff2") format("woff2");}`;
    return [
        face('OpenDyslexic', 'opendyslexic-regular', 400, 'normal'),
        face('OpenDyslexic', 'opendyslexic-bold', 700, 'normal'),
        face('OpenDyslexic', 'opendyslexic-italic', 400, 'italic'),
        face('OpenDyslexic', 'opendyslexic-bolditalic', 700, 'italic'),
        face('Atkinson Hyperlegible', 'atkinson-regular', 400, 'normal'),
        face('Atkinson Hyperlegible', 'atkinson-bold', 700, 'normal'),
        face('Atkinson Hyperlegible', 'atkinson-italic', 400, 'italic'),
        face('Atkinson Hyperlegible', 'atkinson-bolditalic', 700, 'italic'),
    ].join('\n');
}

function readerCss() {
    const theme = THEMES[prefs.theme] || THEMES.light;
    const fontCss = FONTS[prefs.font];
    // Force the reading font (and only then) over the publisher's own, broadly,
    // so accessibility fonts actually take effect on nested elements.
    const fontRule = fontCss ? `* { font-family: ${fontCss} !important; }` : '';
    return `
${fontFaces()}
html {
    color: ${theme.fg} !important;
    background: ${theme.bg} !important;
    color-scheme: ${prefs.theme === 'dark' ? 'dark' : 'light'};
    font-size: ${prefs.size}% !important;
    hanging-punctuation: allow-end last;
}
body { color: ${theme.fg} !important; background: transparent !important; }
${fontRule}
a, a:link, a:visited { color: ${theme.link} !important; }
p, li, blockquote, dd {
    line-height: ${prefs.spacing} !important;
    text-align: ${prefs.justify ? 'justify' : 'start'} !important;
    -webkit-hyphens: ${prefs.hyphenate ? 'auto' : 'manual'};
    hyphens: ${prefs.hyphenate ? 'auto' : 'manual'};
    -webkit-hyphenate-limit-before: 3;
    -webkit-hyphenate-limit-after: 2;
    widows: 2;
    orphans: 2;
}
[align="left"] { text-align: left !important; }
[align="right"] { text-align: right !important; }
[align="center"] { text-align: center !important; }
pre { white-space: pre-wrap !important; }
img, svg, video { max-width: 100% !important; height: auto !important; }
`;
}

// Pushes the current preferences to the open view: the layout attributes on the
// renderer and the injected reading stylesheet.
function applyToView() {
    if (!view || !view.renderer) {
        return;
    }
    view.renderer.setAttribute('flow', prefs.flow);
    view.renderer.setAttribute('max-inline-size', `${prefs.width}px`);
    view.renderer.setAttribute('margin', '48px');
    view.renderer.setAttribute('gap', '6%');
    view.renderer.setStyles?.(readerCss());
}

function applyChromeTheme() {
    document.body.dataset.theme = prefs.theme;
}

// --- Navigation inside each chapter document ---

function handleKey(e) {
    if (e.key === 'ArrowLeft' || e.key === 'PageUp' || e.key === 'h') {
        view.goLeft();
    } else if (e.key === 'ArrowRight' || e.key === 'PageDown' || e.key === 'l' || e.key === ' ') {
        view.goRight();
    } else if (e.key === 'Escape') {
        closeAllPanels();
        return;
    } else {
        return;
    }
    e.preventDefault();
}

function onDocLoad(detail) {
    const doc = detail && detail.doc;
    if (!doc) {
        return;
    }
    doc.addEventListener('keydown', handleKey);
    // Tap the left/right edge to turn the page (paginated mode only); the middle
    // is left free for text selection and following links.
    doc.addEventListener('click', (e) => {
        if (prefs.flow !== 'paginated') {
            return;
        }
        if (e.target && e.target.closest && e.target.closest('a[href]')) {
            return;
        }
        const sel = doc.getSelection && doc.getSelection();
        if (sel && String(sel).length > 0) {
            return;
        }
        const w = doc.documentElement.clientWidth || (doc.body && doc.body.clientWidth) || 0;
        if (!w) {
            return;
        }
        if (e.clientX < w * 0.22) {
            view.goLeft();
        } else if (e.clientX > w * 0.78) {
            view.goRight();
        }
    });
}

function onRelocate(detail) {
    if (!detail) {
        return;
    }
    const slider = document.getElementById('epub-slider');
    const percent = document.getElementById('epub-percent');
    if (typeof detail.fraction === 'number') {
        currentFraction = detail.fraction;
        slider.value = String(detail.fraction);
        percent.textContent = `${Math.round(detail.fraction * 100)} %`;
    }
    if (detail.cfi) {
        currentCfi = detail.cfi;
        savePosition(detail.cfi);
    }
    if (detail.tocItem && detail.tocItem.href) {
        highlightToc(detail.tocItem.href);
    }
}

// --- Table of contents ---

function buildToc(book) {
    const nav = document.getElementById('epub-toc');
    nav.textContent = '';
    const toc = book && book.toc;
    if (!toc || !toc.length) {
        const empty = document.createElement('div');
        empty.className = 'ep-toc-empty';
        empty.textContent = 'Pas de sommaire dans ce livre.';
        nav.appendChild(empty);
        return;
    }
    const add = (items, depth) => {
        for (const item of items) {
            if (item.href) {
                const a = document.createElement('a');
                a.textContent = item.label || '—';
                // No href attribute on purpose: an href="#" can trigger a hash
                // navigation / scroll-to-top on touch browsers that fights our
                // own navigation. We drive it entirely from the click handler.
                a.setAttribute('role', 'link');
                a.tabIndex = 0;
                a.dataset.href = item.href;
                a.dataset.depth = String(depth);
                const target = item.href;
                const go = () => {
                    // Close first so the panel never covers the result, then jump.
                    closeAllPanels();
                    Promise.resolve(view.goTo(target))
                        .catch((err) => console.error('JellyShelf: sommaire — échec goTo', target, err));
                };
                a.addEventListener('click', (e) => { e.preventDefault(); go(); });
                a.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); go(); }
                });
                nav.appendChild(a);
            }
            if (item.subitems && item.subitems.length) {
                add(item.subitems, depth + 1);
            }
        }
    };
    add(toc, 0);
}

function highlightToc(href) {
    const nav = document.getElementById('epub-toc');
    for (const a of nav.querySelectorAll('a')) {
        a.setAttribute('aria-current', a.dataset.href === href ? 'true' : 'false');
    }
}

// --- Controls / panels ---

function closeAllPanels() {
    for (const [panelId, btnId] of [['epub-settings', 'settings-btn'], ['epub-toc', 'toc-btn']]) {
        document.getElementById(panelId).hidden = true;
        document.getElementById(btnId).setAttribute('aria-expanded', 'false');
    }
}

function togglePanel(panelId, btnId) {
    const panel = document.getElementById(panelId);
    const wasHidden = panel.hidden;
    closeAllPanels();
    if (wasHidden) {
        panel.hidden = false;
        document.getElementById(btnId).setAttribute('aria-expanded', 'true');
    }
}

function refreshSettingsUi() {
    document.getElementById('ep-font').value = prefs.font;
    document.getElementById('ep-size-val').textContent = `${prefs.size}%`;
    document.getElementById('ep-spacing-val').textContent = prefs.spacing.toFixed(1);
    const wi = WIDTHS.indexOf(prefs.width);
    document.getElementById('ep-width-val').textContent = WIDTH_LABELS[wi < 0 ? 2 : wi];
    document.getElementById('ep-justify').checked = prefs.justify;
    document.getElementById('ep-hyphenate').checked = prefs.hyphenate;
    for (const b of document.querySelectorAll('.ep-theme')) {
        b.setAttribute('aria-pressed', b.dataset.theme === prefs.theme ? 'true' : 'false');
    }
    for (const b of document.querySelectorAll('.ep-flow')) {
        b.setAttribute('aria-pressed', b.dataset.flow === prefs.flow ? 'true' : 'false');
    }
}

// Commits a preference change: store it, update the panel, re-style the view.
function change(mutate) {
    mutate();
    savePrefs();
    refreshSettingsUi();
    applyToView();
}

function clamp(v, lo, hi) {
    return Math.min(hi, Math.max(lo, v));
}

function stepWidth(delta) {
    let wi = WIDTHS.indexOf(prefs.width);
    if (wi < 0) {
        wi = 2;
    }
    prefs.width = WIDTHS[clamp(wi + delta, 0, WIDTHS.length - 1)];
}

function wireControls(closeReader) {
    document.getElementById('epub-close').addEventListener('click', closeReader);
    document.getElementById('epub-prev').addEventListener('click', () => view.goLeft());
    document.getElementById('epub-next').addEventListener('click', () => view.goRight());

    document.getElementById('settings-btn').addEventListener('click', (e) => {
        e.stopPropagation();
        togglePanel('epub-settings', 'settings-btn');
    });
    document.getElementById('toc-btn').addEventListener('click', (e) => {
        e.stopPropagation();
        togglePanel('epub-toc', 'toc-btn');
    });
    // A click outside an open panel (but not inside the toolbar) closes it.
    document.addEventListener('click', (e) => {
        if (!e.target.closest('#epub-settings, #epub-toc, #settings-btn, #toc-btn')) {
            closeAllPanels();
        }
    });
    window.addEventListener('keydown', handleKey);

    document.getElementById('ep-font').addEventListener('change', (e) => change(() => { prefs.font = e.target.value; }));
    document.getElementById('ep-size-dec').addEventListener('click', () => change(() => { prefs.size = clamp(prefs.size - SIZE_STEP, SIZE_MIN, SIZE_MAX); }));
    document.getElementById('ep-size-inc').addEventListener('click', () => change(() => { prefs.size = clamp(prefs.size + SIZE_STEP, SIZE_MIN, SIZE_MAX); }));
    document.getElementById('ep-spacing-dec').addEventListener('click', () => change(() => { prefs.spacing = clamp(Math.round((prefs.spacing - SPACING_STEP) * 10) / 10, SPACING_MIN, SPACING_MAX); }));
    document.getElementById('ep-spacing-inc').addEventListener('click', () => change(() => { prefs.spacing = clamp(Math.round((prefs.spacing + SPACING_STEP) * 10) / 10, SPACING_MIN, SPACING_MAX); }));
    document.getElementById('ep-width-dec').addEventListener('click', () => change(() => stepWidth(-1)));
    document.getElementById('ep-width-inc').addEventListener('click', () => change(() => stepWidth(1)));
    document.getElementById('ep-justify').addEventListener('change', (e) => change(() => { prefs.justify = e.target.checked; }));
    document.getElementById('ep-hyphenate').addEventListener('change', (e) => change(() => { prefs.hyphenate = e.target.checked; }));

    for (const b of document.querySelectorAll('.ep-theme')) {
        b.addEventListener('click', () => change(() => { prefs.theme = b.dataset.theme; applyChromeTheme(); }));
    }
    for (const b of document.querySelectorAll('.ep-flow')) {
        b.addEventListener('click', () => change(() => { prefs.flow = b.dataset.flow; }));
    }

    const slider = document.getElementById('epub-slider');
    slider.addEventListener('input', (e) => {
        const frac = parseFloat(e.target.value);
        if (!Number.isNaN(frac)) {
            view.goToFraction(frac).catch(() => {});
        }
    });
}

// --- Entry point (called by reader.js) ---

/**
 * Opens an EPUB in the reader. The page is already authenticated; this takes
 * over the DOM, mounts foliate-js and wires the EPUB controls.
 */
export async function startEpub(opts) {
    base = opts.base;
    itemId = opts.itemId;
    showFn = opts.show;
    const { auth, closeReader } = opts;
    state = makeStateClient(base, auth);

    prefs = await loadPrefs();
    document.body.dataset.mode = 'epub';
    applyChromeTheme();
    refreshSettingsUi();
    wireControls(closeReader);

    // Save the exact position if the page is closed between relocate events.
    const flush = () => { if (currentCfi) { state.saveNow(`pos/${itemId}`, { cfi: currentCfi, fraction: currentFraction }); } };
    window.addEventListener('pagehide', flush);
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') { flush(); } });

    showFn('loading', 'Téléchargement du livre…');
    let file;
    try {
        const res = await fetch(`${base}/Items/${encodeURIComponent(itemId)}/Download`, {
            headers: { Authorization: auth },
        });
        if (!res.ok) {
            showFn('error', downloadError(res.status));
            return;
        }
        const blob = await res.blob();
        file = new File([blob], 'book.epub', { type: 'application/epub+zip' });
    } catch (error) {
        console.error(error);
        showFn('error', `Impossible de télécharger ce livre. ${describe(error)}`);
        return;
    }

    showFn('loading', 'Ouverture du livre…');
    try {
        view = document.createElement('foliate-view');
        document.getElementById('epub-root').replaceChildren(view);
        await view.open(file);

        // A chapter whose own CSS/resource fails to load should not abort the
        // whole render — swallow that one resource and keep going.
        view.book.transformTarget?.addEventListener('data', ({ detail }) => {
            detail.data = Promise.resolve(detail.data).catch((e) => {
                console.error(new Error(`Échec du chargement de ${detail.name}`, { cause: e }));
                return '';
            });
        });

        view.addEventListener('load', (e) => onDocLoad(e.detail));
        view.addEventListener('relocate', (e) => onRelocate(e.detail));

        applyToView();
        buildToc(view.book);

        // Resume where the reader last left this book, else start at the text.
        const saved = await loadPosition();
        try {
            await view.init(saved ? { lastLocation: saved } : { showTextStart: true });
        } catch {
            await view.init({ showTextStart: true });
        }

        document.title = titleOf(view.book) || 'JellyShelf — Lecteur';
        showFn('rendered');
    } catch (error) {
        console.error(error);
        showFn('error', `Impossible d'ouvrir ce livre. ${describe(error)}`);
    }
}

function titleOf(book) {
    const t = book && book.metadata && book.metadata.title;
    if (!t) {
        return '';
    }
    if (typeof t === 'string') {
        return t;
    }
    const keys = Object.keys(t);
    return keys.length ? t[keys[0]] : '';
}

function downloadError(statusCode) {
    switch (statusCode) {
        case 401: return 'Votre session Jellyfin a expiré. Reconnectez-vous à Jellyfin.';
        case 403: return "Vous n'avez pas la permission de télécharger ce livre.";
        case 404: return 'Livre introuvable dans votre bibliothèque Jellyfin.';
        default: return `Le livre n'a pas pu être chargé (HTTP ${statusCode}).`;
    }
}

function describe(error) {
    return error instanceof Error ? `${error.name}: ${error.message}` : String(error);
}
