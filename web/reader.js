// JellyShelf reader — Phase 0 (PDF).
// Loaded as a module from {base}/JellyShelf/reader.js. It authenticates itself
// against Jellyfin by reading jellyfin-web's session from the same-origin local
// storage, downloads the book through Jellyfin's own /Items/{id}/Download, and
// renders it with the vendored pdf.js.
//
// Zoom is by mouse wheel (desktop) or two-finger pinch (touch). The page is
// moved by dragging (grab to pan). In the horizontal reading direction the book
// is shown one page at a time; a click on the right half turns to the next page
// and a click on the left half to the previous one.

import * as pdfjsLib from './pdf.mjs';
import { makeStateClient } from './reader-state.js';

pdfjsLib.GlobalWorkerOptions.workerSrc = new URL('./pdf.worker.mjs', import.meta.url).href;

const CLIENT = 'JellyShelf';
const VERSION = '1.4.0';
const CREDENTIALS_KEY = 'jellyfin_credentials';
const WEB_DEVICE_ID_KEY = '_deviceId2';
const OWN_DEVICE_ID_KEY = 'jellyshelf_device_id';

const MIN_SCALE = 0.25;
const MAX_SCALE = 2; // 200% hard cap
const EDGE_TURN = 0.25; // fraction of each side that turns the page; the middle is for zoom
const PINCH_SENSITIVITY = 0.5; // <1 softens two-finger zoom (1 = raw finger-distance ratio)

const statusEl = document.getElementById('status');
const pagesEl = document.getElementById('pages');
const viewerEl = document.getElementById('viewer');
const pageIndicator = document.getElementById('page-indicator');

const state = {
    source: null,       // the open book: a PDF source or a comic-archive source
    scale: 1,
    renderedScale: 1,   // the scale the current canvases were drawn at
    fit: 'width',       // 'width' | 'page' | null (manual)
    rotation: 0,
    orientation: 'horizontal',
    current: 1,
    rendering: false,
    renderPending: false,
    renderGen: 0,       // bumped on every render request; a running render aborts when it changes
    anchor: null,       // centre ratio to keep fixed across a zoom re-render
    itemId: null,       // the open book, for the "close" button
    stateClient: null,  // per-account store for resuming where you left off
    pendingScrollRatio: null, // vertical scroll to restore after the first render
};

function show(stateName, message = '') {
    document.body.dataset.state = stateName;
    if (message) {
        statusEl.textContent = message;
    }
}

// --- Jellyfin session (jellyfin-web storage is an internal detail kept here) ---

function readSession() {
    let stored;
    try {
        stored = JSON.parse(localStorage.getItem(CREDENTIALS_KEY) || 'null');
    } catch {
        return null;
    }
    const servers = stored && Array.isArray(stored.Servers) ? stored.Servers : [];
    const usable = servers.filter((s) => s && s.AccessToken && s.UserId);
    if (!usable.length) {
        return null;
    }
    const sameOrigin = usable.filter((s) =>
        [s.ManualAddress, s.LocalAddress, s.RemoteAddress].some((a) => originOf(a) === location.origin));
    const pick = (sameOrigin.length ? sameOrigin : usable)
        .sort((a, b) => (b.DateLastAccessed || 0) - (a.DateLastAccessed || 0))[0];
    return { accessToken: pick.AccessToken, userId: pick.UserId };
}

function originOf(address) {
    try {
        return new URL(address).origin;
    } catch {
        return null;
    }
}

function deviceId() {
    const existing = localStorage.getItem(WEB_DEVICE_ID_KEY) || localStorage.getItem(OWN_DEVICE_ID_KEY);
    if (existing) {
        return existing;
    }
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
    const id = `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
    localStorage.setItem(OWN_DEVICE_ID_KEY, id);
    return id;
}

function authorizationHeader(session) {
    const fields = [
        ['Client', CLIENT],
        ['Device', 'JellyShelf Reader'],
        ['DeviceId', deviceId()],
        ['Version', VERSION],
        ['Token', session.accessToken],
    ];
    return 'MediaBrowser ' + fields.map(([k, v]) => `${k}="${encodeURIComponent(v)}"`).join(', ');
}

// The server base path for this reader page: a configured Jellyfin BaseUrl, or ''.
function serverBasePath() {
    const index = location.pathname.toLowerCase().lastIndexOf('/jellyshelf');
    return index > 0 ? location.pathname.slice(0, index) : '';
}

function loginUrl() {
    return `${serverBasePath()}/web/#/login`;
}

// --- Loading ---

// Leaves the reader: back to where the user came from, else the book's detail page.
function closeReader() {
    if (window.history.length > 1) {
        window.history.back();
    } else {
        const base = serverBasePath();
        location.assign(state.itemId
            ? `${base}/web/#/details?id=${encodeURIComponent(state.itemId)}`
            : `${base}/web/`);
    }
}

async function start() {
    const itemId = new URLSearchParams(location.search).get('id');
    state.itemId = itemId;
    const session = readSession();
    if (!session) {
        location.replace(loginUrl());
        return;
    }
    if (!itemId) {
        show('error', 'Aucun livre sélectionné. Ouvrez un livre depuis votre bibliothèque Jellyfin.');
        return;
    }

    const base = serverBasePath();
    const auth = authorizationHeader(session);
    show('loading', 'Ouverture…');
    try {
        const info = await fetchInfo(base, itemId, auth);
        if (info && info.kind === 'epub') {
            // Reflowable text is a different world from the canvas (paginated
            // HTML, not raster pages): a dedicated module takes over the page.
            const { startEpub } = await import('./epub-reader.js');
            await startEpub({ base, itemId, auth, show, closeReader });
            return;
        }
        if (info && info.kind === 'comic' && info.pageCount > 0) {
            state.source = comicSource(base, itemId, auth, info.pageCount);
        } else {
            // PDF, or an unknown kind we try to read as a PDF.
            show('loading', 'Téléchargement du livre…');
            const response = await fetch(`${base}/Items/${encodeURIComponent(itemId)}/Download`, {
                headers: { Authorization: auth },
            });
            if (!response.ok) {
                show('error', downloadError(response.status));
                return;
            }
            const data = await response.arrayBuffer();
            state.source = pdfSource(await pdfjsLib.getDocument({ data }).promise);
        }

        state.stateClient = makeStateClient(base, auth);
        await applyCanvasResume();
        await renderAll();
        restoreCanvasScroll();
        show('rendered');
        updatePageIndicator();
    } catch (error) {
        console.error(error);
        show('error', `Impossible d'ouvrir ce livre. ${describe(error)}`);
    }
}

// Asks the plugin what kind of book this is (comic archive, PDF, …). Null when it
// cannot be read, in which case the caller falls back to trying a PDF.
async function fetchInfo(base, itemId, auth) {
    try {
        const res = await fetch(`${base}/JellyShelf/Books/${encodeURIComponent(itemId)}/Info`, {
            headers: { Authorization: auth },
        });
        return res.ok ? await res.json() : null;
    } catch {
        return null;
    }
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

// --- Rendering ---

// The scale for the current fit mode given a page's unscaled viewport.
function scaleFor(viewport) {
    const avail = viewerEl.clientWidth - 24;
    const availH = viewerEl.clientHeight - 24;
    // Fit modes may shrink a large page below 25% so the whole width/page shows;
    // only the 200% cap applies to them. Manual zoom is clamped to [MIN, MAX].
    if (state.fit === 'width') {
        return Math.min(MAX_SCALE, avail / viewport.width);
    }
    if (state.fit === 'page') {
        return Math.min(MAX_SCALE, Math.min(avail / viewport.width, availH / viewport.height));
    }
    return state.scale;
}

// A cache of rendered page canvases for the horizontal (page-by-page) mode, so
// a page already seen turns back instantly. It is keyed by the render settings
// (fit/scale/rotation/size); a change there empties it.
const pageCache = { key: '', pages: new Map() };
const MAX_CACHED = 6;

// Releasing a canvas's backing store promptly (not just dropping the reference)
// matters on mobile, where canvas memory is tightly capped.
function freeCanvas(canvas) {
    if (canvas instanceof HTMLCanvasElement) {
        canvas.width = 0;
        canvas.height = 0;
    }
}

function renderKey() {
    return state.fit
        ? `fit:${state.fit}|${state.rotation}|${viewerEl.clientWidth}x${viewerEl.clientHeight}`
        : `man:${state.scale.toFixed(3)}|${state.rotation}`;
}

async function renderAll() {
    if (!state.source) {
        return;
    }
    // Supersede any render in progress: bump the generation and cancel the pdf.js
    // tasks still running, so rapid zoom never piles up concurrent renders.
    state.renderGen++;
    cancelActiveRenders();
    if (state.rendering) {
        state.renderPending = true;
        return;
    }
    state.rendering = true;
    try {
        do {
            state.renderPending = false;
            const myGen = state.renderGen;

            // What to keep fixed across this render: a zoom anchor (centre of the
            // page) if a zoom set one, else the vertical scroll position.
            const anchor = state.anchor;
            state.anchor = null;
            const ratio = (!anchor && state.orientation === 'vertical' && pagesEl.scrollHeight > 0)
                ? viewerEl.scrollTop / pagesEl.scrollHeight
                : null;

            pagesEl.style.transform = 'none';

            // Stop any lazy vertical rendering from a previous pass.
            if (verticalObserver) {
                verticalObserver.disconnect();
                verticalObserver = null;
            }

            if (state.orientation === 'horizontal') {
                await showCurrent(myGen);
            } else {
                await buildVertical(myGen);
            }

            if (myGen === state.renderGen) {
                state.renderedScale = state.scale;
                updateZoomLabel();
                applyScroll(anchor, ratio);
                saveCanvasState();
            }
        } while (state.renderPending);
    } finally {
        state.rendering = false;
    }
}

// Restores the scroll position after a render: centred on the zoom anchor, or at
// the kept vertical ratio, clamped to what the content allows.
function applyScroll(anchor, ratio) {
    const maxX = Math.max(0, pagesEl.scrollWidth - viewerEl.clientWidth);
    const maxY = Math.max(0, pagesEl.scrollHeight - viewerEl.clientHeight);
    if (anchor) {
        viewerEl.scrollLeft = Math.min(maxX, Math.max(0, anchor.rx * pagesEl.scrollWidth - anchor.fx));
        viewerEl.scrollTop = Math.min(maxY, Math.max(0, anchor.ry * pagesEl.scrollHeight - anchor.fy));
    } else if (ratio != null) {
        viewerEl.scrollTop = Math.min(maxY, ratio * pagesEl.scrollHeight);
    }
}

// Shows the current page from the cache when possible, then quietly renders the
// neighbours so the next/previous turn is instant.
async function showCurrent(gen) {
    const key = renderKey();
    if (key !== pageCache.key) {
        pageCache.key = key;
        pageCache.pages.clear();
    }

    let canvas = pageCache.pages.get(state.current);
    if (!canvas) {
        canvas = await renderPageToCanvas(state.current);
        if (!canvas) {
            return; // cancelled/failed — a newer render will show the page
        }
        if (renderKey() === pageCache.key) {
            pageCache.pages.set(state.current, canvas);
        }
    }
    if (gen !== undefined && gen !== state.renderGen) {
        return; // a newer render (page turn, zoom, mode switch) took over
    }
    pagesEl.replaceChildren(canvas);
    prefetchNeighbours(key);
}

function prefetchNeighbours(key) {
    const wanted = [state.current + 1, state.current - 1]
        .filter((n) => n >= 1 && n <= state.source.numPages && !pageCache.pages.has(n));
    void (async () => {
        for (const n of wanted) {
            if (pageCache.key !== key) {
                return;
            }
            try {
                // eslint-disable-next-line no-await-in-loop
                const canvas = await renderPageToCanvas(n, false);
                if (canvas && pageCache.key === key) {
                    pageCache.pages.set(n, canvas);
                }
            } catch { /* a page that fails to prefetch is just rendered on demand */ }
        }
        // Keep the cache small: drop (and free) the pages furthest from the current one.
        if (pageCache.pages.size > MAX_CACHED) {
            [...pageCache.pages.keys()]
                .sort((a, b) => Math.abs(b - state.current) - Math.abs(a - state.current))
                .slice(0, pageCache.pages.size - MAX_CACHED)
                .forEach((n) => { freeCanvas(pageCache.pages.get(n)); pageCache.pages.delete(n); });
        }
    })();
}

// Backing-store budget: a page canvas is drawn at the capped device pixel ratio,
// but never beyond this many pixels, so rapid zooming can't exhaust the browser's
// canvas memory and crash the tab (especially on mobile).
const MAX_DEVICE_RATIO = 2;
const MAX_CANVAS_PIXELS = 4_000_000;

// pdf.js render tasks currently running. Cancelling them when a new render
// supersedes keeps rapid zoom from piling up dozens of concurrent renders.
const activeTasks = new Set();

function cancelActiveRenders() {
    for (const task of activeTasks) {
        try {
            task.cancel();
        } catch { /* already finished */ }
    }
    activeTasks.clear();
}

// Renders one page to a detached canvas, using whichever source is open. Returns
// null when the render is cancelled (superseded) or fails, so callers skip it and
// retry later. When `track`, it records the scale used (so the zoom label follows
// the page on screen, not a neighbour being prefetched).
async function renderPageToCanvas(n, track = true) {
    const natural = await state.source.naturalSize(n).catch(() => null);
    if (!natural) {
        return null;
    }

    // Logical (CSS) size at scale 1, with rotation swapping width and height.
    const rot = ((state.rotation % 360) + 360) % 360;
    const swap = rot === 90 || rot === 270;
    const vw = swap ? natural.height : natural.width;
    const vh = swap ? natural.width : natural.height;

    const scale = scaleFor({ width: vw, height: vh });
    if (track) {
        state.scale = scale;
    }

    const cssW = vw * scale;
    const cssH = vh * scale;
    const dev = Math.min(window.devicePixelRatio || 1, MAX_DEVICE_RATIO);
    let bw = cssW * dev;
    let bh = cssH * dev;
    const area = bw * bh;
    if (area > MAX_CANVAS_PIXELS) {
        const f = Math.sqrt(MAX_CANVAS_PIXELS / area);
        bw *= f;
        bh *= f;
    }

    const canvas = document.createElement('canvas');
    canvas.dataset.page = String(n);
    canvas.width = Math.max(1, Math.floor(bw));
    canvas.height = Math.max(1, Math.floor(bh));
    canvas.style.width = `${Math.floor(cssW)}px`;
    canvas.style.height = `${Math.floor(cssH)}px`;

    try {
        await state.source.drawInto(n, canvas, scale, rot);
    } catch {
        return null; // cancelled or failed
    }
    return canvas;
}

// --- Book sources ---
// Each source exposes: numPages, naturalSize(n) -> {width,height} (unrotated, at
// scale 1), and drawInto(n, canvas, scale, rotation) which fills the canvas.

function pdfSource(doc) {
    return {
        numPages: doc.numPages,
        async naturalSize(n) {
            const page = await doc.getPage(n);
            const v = page.getViewport({ scale: 1, rotation: 0 });
            return { width: v.width, height: v.height };
        },
        async drawInto(n, canvas, scale, rot) {
            const page = await doc.getPage(n);
            const viewport = page.getViewport({ scale, rotation: rot });
            const task = page.render({
                canvasContext: canvas.getContext('2d'),
                viewport,
                transform: [canvas.width / viewport.width, 0, 0, canvas.height / viewport.height, 0, 0],
            });
            activeTasks.add(task);
            try {
                await task.promise;
            } finally {
                activeTasks.delete(task);
            }
        },
    };
}

function comicSource(base, itemId, auth, pageCount) {
    const urls = new Map();  // 1-based page -> object URL
    const order = [];
    const dims = new Map();  // 1-based page -> {width,height}
    const URL_CAP = 8;

    async function pageUrl(n) {
        if (urls.has(n)) {
            return urls.get(n);
        }
        const res = await fetch(`${base}/JellyShelf/Books/${encodeURIComponent(itemId)}/Pages/${n - 1}`, {
            headers: { Authorization: auth },
        });
        if (!res.ok) {
            throw new Error(`page ${n}: HTTP ${res.status}`);
        }
        const url = URL.createObjectURL(await res.blob());
        urls.set(n, url);
        order.push(n);
        while (order.length > URL_CAP) {
            const old = order.shift();
            if (old !== n && urls.has(old)) {
                URL.revokeObjectURL(urls.get(old));
                urls.delete(old);
            }
        }
        return url;
    }

    function loadImage(url) {
        return new Promise((resolve, reject) => {
            const img = new Image();
            img.onload = () => resolve(img);
            img.onerror = () => reject(new Error('image decode failed'));
            img.src = url;
        });
    }

    return {
        numPages: pageCount,
        async naturalSize(n) {
            if (dims.has(n)) {
                return dims.get(n);
            }
            const img = await loadImage(await pageUrl(n));
            const d = { width: img.naturalWidth, height: img.naturalHeight };
            dims.set(n, d);
            return d;
        },
        async drawInto(n, canvas, scale, rot) {
            drawRotated(canvas, await loadImage(await pageUrl(n)), rot);
        },
    };
}

// Draws an image to fill the (already rotation-sized) canvas, applying rotation.
function drawRotated(canvas, img, rot) {
    const ctx = canvas.getContext('2d');
    ctx.imageSmoothingQuality = 'high';
    ctx.save();
    if (rot === 90) {
        ctx.translate(canvas.width, 0);
        ctx.rotate(Math.PI / 2);
        ctx.drawImage(img, 0, 0, canvas.height, canvas.width);
    } else if (rot === 180) {
        ctx.translate(canvas.width, canvas.height);
        ctx.rotate(Math.PI);
        ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
    } else if (rot === 270) {
        ctx.translate(0, canvas.height);
        ctx.rotate(-Math.PI / 2);
        ctx.drawImage(img, 0, 0, canvas.height, canvas.width);
    } else {
        ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
    }
    ctx.restore();
}

// --- Vertical: lazy, on-demand rendering ---
// Every page gets a reserved-size placeholder so the scrollbar is correct, but a
// page is only drawn when it scrolls near the view, and dropped when it moves far
// away — so a long book never renders all its pages at once.

let verticalObserver = null;

async function buildVertical(myGen) {
    pageCache.key = '';
    pageCache.pages.clear();
    pagesEl.textContent = '';

    const ref = await referenceSize();
    if (myGen !== state.renderGen) {
        return;
    }

    for (let n = 1; n <= state.source.numPages; n++) {
        const slot = document.createElement('div');
        slot.className = 'slot';
        slot.dataset.page = String(n);
        slot.style.width = `${ref.w}px`;
        slot.style.height = `${ref.h}px`;
        pagesEl.appendChild(slot);
    }

    verticalObserver = new IntersectionObserver((entries) => {
        for (const entry of entries) {
            const slot = entry.target;
            if (entry.isIntersecting) {
                slot.classList.add('visible');
                void renderSlot(slot, Number(slot.dataset.page));
            } else {
                slot.classList.remove('visible');
                clearSlot(slot);
            }
        }
    }, { root: viewerEl, rootMargin: '150% 0px' });

    for (const slot of pagesEl.children) {
        verticalObserver.observe(slot);
    }
}

// The reserved page size, from the first page at the current scale (also sets the
// zoom label). Pages of a different size correct their slot when they render.
async function referenceSize() {
    const natural = await state.source.naturalSize(1);
    const rot = ((state.rotation % 360) + 360) % 360;
    const swap = rot === 90 || rot === 270;
    const vw = swap ? natural.height : natural.width;
    const vh = swap ? natural.width : natural.height;
    const scale = scaleFor({ width: vw, height: vh });
    state.scale = scale;
    return { w: Math.floor(vw * scale), h: Math.floor(vh * scale) };
}

async function renderSlot(slot, n) {
    if (slot.dataset.rendered === '1' || slot.dataset.rendering === '1') {
        return;
    }
    slot.dataset.rendering = '1';
    try {
        const canvas = await renderPageToCanvas(n, false);
        if (!canvas) {
            return; // cancelled/failed — retried when it next re-enters the view
        }
        slot.style.width = canvas.style.width;
        slot.style.height = canvas.style.height;
        slot.replaceChildren(canvas);
        slot.dataset.rendered = '1';
    } catch {
        // Leave the slot empty; it retries when it next re-enters the view.
    } finally {
        slot.dataset.rendering = '';
    }
}

function clearSlot(slot) {
    if (slot.dataset.rendered !== '1') {
        return;
    }
    freeCanvas(slot.firstElementChild);
    slot.textContent = '';
    slot.dataset.rendered = '';
    // Keep the reserved size so scrolling stays stable.
}

function updateZoomLabel() {
    document.getElementById('zoom-level').textContent = `${Math.round(state.scale * 100)}%`;
}

function updatePageIndicator() {
    if (!state.source) {
        return;
    }
    pageIndicator.textContent = state.orientation === 'horizontal'
        ? `${state.current} / ${state.source.numPages}`
        : `${state.source.numPages} pages`;
}

// --- Resume: remember where this account last was in this book ---

// Applies a saved reading position (page, reading direction, zoom, rotation)
// before the first render; a vertical scroll offset is restored afterwards.
async function applyCanvasResume() {
    state.current = 1;
    state.pendingScrollRatio = null;
    if (!state.stateClient || !state.itemId) {
        return;
    }
    const saved = await state.stateClient.load(`pos/${state.itemId}`);
    if (!saved || saved.mode !== 'canvas') {
        return;
    }
    if (saved.orientation === 'vertical' || saved.orientation === 'horizontal') {
        state.orientation = saved.orientation;
    }
    if (Number.isFinite(saved.rotation)) {
        state.rotation = ((saved.rotation % 360) + 360) % 360;
    }
    if (saved.fit === 'width' || saved.fit === 'page') {
        state.fit = saved.fit;
    } else if (Number.isFinite(saved.scale)) {
        state.fit = null;
        state.scale = saved.scale;
    }
    if (Number.isFinite(saved.page)) {
        state.current = Math.min(state.source.numPages, Math.max(1, Math.round(saved.page)));
    }
    if (Number.isFinite(saved.scrollRatio)) {
        state.pendingScrollRatio = saved.scrollRatio;
    }
    applyOrientation();
}

function restoreCanvasScroll() {
    if (state.orientation === 'vertical' && state.pendingScrollRatio != null) {
        const maxY = Math.max(0, pagesEl.scrollHeight - viewerEl.clientHeight);
        viewerEl.scrollTop = Math.min(maxY, state.pendingScrollRatio * pagesEl.scrollHeight);
    }
    state.pendingScrollRatio = null;
}

function canvasPosition() {
    const scrollRatio = (state.orientation === 'vertical' && pagesEl.scrollHeight > 0)
        ? viewerEl.scrollTop / pagesEl.scrollHeight
        : 0;
    return {
        mode: 'canvas',
        page: state.current,
        orientation: state.orientation,
        fit: state.fit,
        scale: state.scale,
        rotation: state.rotation,
        scrollRatio,
    };
}

function saveCanvasState() {
    if (state.stateClient && state.source && state.itemId) {
        state.stateClient.save(`pos/${state.itemId}`, canvasPosition());
    }
}

// --- Zoom (wheel / pinch), with a live CSS preview then a crisp re-render ---

let liveTimer = null;

// Zooms to `target`, keeping the point under (focalX, focalY) — client coords of
// the mouse or the midpoint of two fingers — fixed on screen. Without a focal
// point (keyboard), it keeps the centre of the view.
function zoomTo(target, focalX, focalY, defer = false) {
    const rect = viewerEl.getBoundingClientRect();
    const fx = focalX === undefined ? viewerEl.clientWidth / 2 : focalX - rect.left;
    const fy = focalY === undefined ? viewerEl.clientHeight / 2 : focalY - rect.top;

    // The content point under the focal point, as a ratio, plus where on screen
    // to put it back after the re-render.
    const contentX = viewerEl.scrollLeft + fx;
    const contentY = viewerEl.scrollTop + fy;
    state.anchor = {
        rx: contentX / (pagesEl.scrollWidth || 1),
        ry: contentY / (pagesEl.scrollHeight || 1),
        fx,
        fy,
    };

    state.fit = null;
    state.scale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, target));

    // Instant visual feedback: scale the already-drawn pages about the focal
    // point, then redraw sharp and restore the anchor.
    const k = state.scale / state.renderedScale;
    pagesEl.style.transformOrigin = `${contentX}px ${contentY}px`;
    pagesEl.style.transform = `scale(${k})`;
    updateZoomLabel();

    // During a continuous gesture (pinch) we defer the crisp re-render to the end,
    // so the transform preview never resets mid-gesture (which looked like a jump).
    clearTimeout(liveTimer);
    if (!defer) {
        liveTimer = setTimeout(() => renderAll(), 160);
    }
}

function zoomBy(factor, focalX, focalY) {
    zoomTo(state.scale * factor, focalX, focalY);
}

// --- Controls ---

// The ☰ menu groups the less-frequent actions: Actualiser, Largeur, Page, Rotation.
function wireMenu() {
    const btn = document.getElementById('menu-btn');
    const menu = document.getElementById('menu');

    const close = () => { menu.hidden = true; btn.setAttribute('aria-expanded', 'false'); };
    const open = () => { menu.hidden = false; btn.setAttribute('aria-expanded', 'true'); };

    btn.addEventListener('click', (e) => {
        e.stopPropagation();
        if (menu.hidden) { open(); } else { close(); }
    });
    document.addEventListener('click', (e) => {
        if (!menu.hidden && e.target !== btn && !menu.contains(e.target)) { close(); }
    });
    window.addEventListener('keydown', (e) => { if (e.key === 'Escape') { close(); } });

    const item = (id, fn) => document.getElementById(id).addEventListener('click', () => { fn(); close(); });
    item('refresh', () => { pageCache.key = ''; pageCache.pages.clear(); renderAll(); });
    item('fit-width', () => { state.fit = 'width'; renderAll(); });
    item('fit-page', () => { state.fit = 'page'; renderAll(); });
    item('rotate', () => { state.rotation = (state.rotation + 90) % 360; renderAll(); });
}

function wireControls() {
    applyOrientation(); // reflect the default reading direction before the first render
    wireMenu();
    document.getElementById('close').addEventListener('click', closeReader);
    document.getElementById('zoom-level').addEventListener('click', () => { state.fit = 'width'; renderAll(); });
    document.getElementById('orientation').addEventListener('click', toggleOrientation);

    // Desktop: the wheel zooms (no modifier needed).
    viewerEl.addEventListener('wheel', (e) => {
        e.preventDefault();
        zoomBy(e.deltaY < 0 ? 1.1 : 1 / 1.1, e.clientX, e.clientY);
    }, { passive: false });

    // Keyboard stays available: + / - / 0 and page turns in horizontal mode.
    // (Inert once the EPUB reader has taken over the page — it binds its own.)
    window.addEventListener('keydown', (e) => {
        if (document.body.dataset.mode === 'epub') { return; }
        if (e.key === '+' || e.key === '=') { zoomBy(1.25); }
        else if (e.key === '-') { zoomBy(1 / 1.25); }
        else if (e.key === '0') { state.fit = 'width'; renderAll(); }
        else if (state.orientation === 'horizontal' && (e.key === 'ArrowRight' || e.key === 'PageDown')) { goTo(state.current + 1); }
        else if (state.orientation === 'horizontal' && (e.key === 'ArrowLeft' || e.key === 'PageUp')) { goTo(state.current - 1); }
    });

    wirePointer();
    window.addEventListener('resize', () => { if (state.fit) { renderAll(); } });

    // Resume support: save the vertical scroll position (debounced) and make sure
    // the exact position is written if the page is closed.
    viewerEl.addEventListener('scroll', () => {
        if (state.stateClient && state.orientation === 'vertical') {
            saveCanvasState();
        }
    }, { passive: true });
    const flush = () => {
        if (state.stateClient && state.source && state.itemId) {
            state.stateClient.saveNow(`pos/${state.itemId}`, canvasPosition());
        }
    };
    window.addEventListener('pagehide', flush);
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') { flush(); } });
}

function applyOrientation() {
    document.body.dataset.orientation = state.orientation;
    document.getElementById('orientation').textContent = state.orientation === 'vertical' ? 'Vertical' : 'Horizontal';
}

function toggleOrientation() {
    state.orientation = state.orientation === 'vertical' ? 'horizontal' : 'vertical';
    applyOrientation();
    // Clear whatever is shown so an aborted render's pages don't flash in the new layout.
    pagesEl.textContent = '';
    viewerEl.scrollTo(0, 0);
    renderAll();
    updatePageIndicator();
}

function goTo(n) {
    if (!state.source) {
        return;
    }
    const clamped = Math.min(state.source.numPages, Math.max(1, n));
    if (clamped === state.current) {
        return;
    }
    state.current = clamped;
    if (state.orientation === 'horizontal') {
        viewerEl.scrollTo(0, 0);
        renderAll();
    }
    updatePageIndicator();
}

// One handler for every pointer: two touch points pinch-zoom; a single pointer
// drags to pan; a single pointer that did not move turns the page (horizontal).
function wirePointer() {
    const touches = new Map();
    let pinchDist = 0;
    let pinchScale = 1;
    let drag = null;
    let lastTap = null; // for double-tap / double-click detection
    let pinchActive = false;

    viewerEl.addEventListener('pointerdown', (e) => {
        if (e.pointerType === 'touch') {
            touches.set(e.pointerId, e);
            if (touches.size === 2) {
                drag = null; // a second finger means a pinch, not a pan/tap
                pinchActive = true;
                const [a, b] = [...touches.values()];
                pinchDist = Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY);
                pinchScale = state.scale;
                return;
            }
        }
        // Begin a possible pan / tap with the primary pointer.
        drag = { x: e.clientX, y: e.clientY, sl: viewerEl.scrollLeft, st: viewerEl.scrollTop, moved: false, touch: e.pointerType === 'touch' };
    });

    viewerEl.addEventListener('pointermove', (e) => {
        if (touches.has(e.pointerId)) {
            touches.set(e.pointerId, e);
        }
        if (touches.size === 2) {
            const [a, b] = [...touches.values()];
            const dist = Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY);
            if (pinchDist > 0) {
                // Damp the raw finger-distance ratio so the zoom feels less abrupt,
                // and anchor it on the midpoint between the two fingers.
                const ratio = 1 + (dist / pinchDist - 1) * PINCH_SENSITIVITY;
                zoomTo(pinchScale * ratio, (a.clientX + b.clientX) / 2, (a.clientY + b.clientY) / 2, true);
            }
            return;
        }
        if (drag) {
            const dx = e.clientX - drag.x;
            const dy = e.clientY - drag.y;
            if (!drag.moved && Math.hypot(dx, dy) > 6) {
                drag.moved = true;
                viewerEl.classList.add('panning');
            }
            // Mouse pans by dragging; touch keeps the browser's native scrolling.
            if (drag.moved && !drag.touch) {
                viewerEl.scrollLeft = drag.sl - dx;
                viewerEl.scrollTop = drag.st - dy;
            }
        }
    });

    const end = (e) => {
        touches.delete(e.pointerId);
        if (touches.size < 2) {
            pinchDist = 0;
            if (pinchActive) {
                // Pinch finished: now do the one crisp re-render at the final zoom.
                pinchActive = false;
                renderAll();
            }
        }
        if (drag) {
            if (!drag.moved) {
                const now = performance.now();
                const isDouble = lastTap
                    && now - lastTap.t < 300
                    && Math.hypot(e.clientX - lastTap.x, e.clientY - lastTap.y) < 30;
                if (isDouble) {
                    // Double tap / double click: fit to width.
                    lastTap = null;
                    state.fit = 'width';
                    renderAll();
                } else {
                    lastTap = { t: now, x: e.clientX, y: e.clientY };
                    // A single tap turns the page only on the left/right edge; the
                    // middle is left free (pinch-to-zoom, double-tap).
                    if (state.orientation === 'horizontal') {
                        const rect = viewerEl.getBoundingClientRect();
                        const x = (e.clientX - rect.left) / rect.width;
                        if (x <= EDGE_TURN) {
                            goTo(state.current - 1);
                        } else if (x >= 1 - EDGE_TURN) {
                            goTo(state.current + 1);
                        }
                    }
                }
            }
            viewerEl.classList.remove('panning');
            drag = null;
        }
    };
    viewerEl.addEventListener('pointerup', end);
    viewerEl.addEventListener('pointercancel', end);
}

wireControls();
void start();
